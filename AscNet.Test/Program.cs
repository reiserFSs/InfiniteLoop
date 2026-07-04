using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Common;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.SDKServer.Models;
using AscNet.Table.V2.share.guide;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.mainline;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.fashion;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MongoDB.Driver;
using System.Reflection;
using System.Reflection.Emit;
using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;

namespace AscNet.Test
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                UseResourceWorkingDirectory();
                if (args.Contains("--notify-login-compat-only"))
                {
                    ValidateNotifyLoginCurrentClientCompatibilityShape();
                    return;
                }

                if (args.Contains("--stage-bookmark-compat-only"))
                {
                    ValidateStageBookmarkCompatibilityShape();
                    return;
                }

                if (args.Contains("--mainline2-exhibition-compat-only"))
                {
                    ValidateMainLine2UpdateExhibitionChapterCompatibility();
                    return;
                }

                if (args.Contains("--mainline-treasure-reward-compat-only"))
                {
                    ValidateMainLineTreasureRewardCompatibility();
                    return;
                }

                if (args.Contains("--boss-single-login-compat-only"))
                {
                    ValidateBossSingleLoginCompatibilityShape();
                    return;
                }

                if (args.Contains("--guide-table-compat-only"))
                {
                    ValidateCurrentClientGuideTableCompatibility();
                    return;
                }

                if (args.Contains("--player-cost-time-upload-compat-only"))
                {
                    ValidatePlayerCostTimeUploadCompatibility();
                    return;
                }

                if (args.Contains("--record-player-point-compat-only"))
                {
                    ValidateRecordPlayerPointCompatibility();
                    return;
                }

                if (args.Contains("--player-gender-compat-only"))
                {
                    ValidatePlayerGenderCompatibility();
                    return;
                }

                if (args.Contains("--board-mutual-push-compat-only"))
                {
                    ValidateBoardMutualClientPushCompatibility();
                    return;
                }

                if (args.Contains("--character-progression-persistence-compat-only"))
                {
                    ValidateCharacterProgressionPersistenceCompatibility();
                    return;
                }

                if (args.Contains("--exp-level-compat-only"))
                {
                    ValidateExpLevelCompatibility();
                    return;
                }

                if (args.Contains("--story-course-reward-compat-only"))
                {
                    ValidateStoryCourseRewardCompatibility();
                    return;
                }

                if (args.Contains("--pr2-quality-compat-only"))
                {
                    ValidatePr2QualityCompatibility();
                    return;
                }

                if (args.Contains("--inventory-equip-compat-only"))
                {
                    ValidateInventoryEquipCompatibility();
                    return;
                }

                if (args.Contains("--draw-compat-only"))
                {
                    ValidateDrawCompatibility();
                    return;
                }

                if (args.Contains("--command-compat-only"))
                {
                    ValidateCommandCompatibility();
                    return;
                }

                if (args.Contains("--current-client-notice-endpoints-only"))
                {
                    ValidateCurrentClientNoticeEndpoints().GetAwaiter().GetResult();
                    return;
                }

                _ = JsonConvert.DeserializeObject<NotifyLogin>(File.ReadAllText(ResourcePath("Data", "NotifyLogin.json")))!;
                _ = JsonConvert.DeserializeObject<NotifyTaskData>(File.ReadAllText(ResourcePath("Data", "NotifyTaskData.json")))!;
                ValidateNotifyLoginCurrentClientCompatibilityShape();
                ValidateStageBookmarkCompatibilityShape();
                ValidateMainLine2UpdateExhibitionChapterCompatibility();
                ValidateMainLineTreasureRewardCompatibility();
                ValidateBossSingleLoginCompatibilityShape();
                ValidateCurrentClientGuideTableCompatibility();
                ValidatePlayerCostTimeUploadCompatibility();
                ValidateRecordPlayerPointCompatibility();
                ValidateBoardMutualClientPushCompatibility();
                ValidatePlayerGenderCompatibility();
                ValidateCharacterProgressionPersistenceCompatibility();
                ValidateExpLevelCompatibility();
                ValidateStoryCourseRewardCompatibility();
                ValidatePr2QualityCompatibility();
                ValidateInventoryEquipCompatibility();
                ValidateDrawCompatibility();
                ValidateCommandCompatibility();
                ValidateCurrentClientNoticeFixtures();
                ValidateCurrentClientNoticeEndpoints().GetAwaiter().GetResult();
                ValidateSteamClientConfig();
                ValidateKuroSdkCompatibilityEndpoints().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Environment.ExitCode = 1;
            }
        }

        private static void ValidateNotifyLoginCurrentClientCompatibilityShape()
        {
            (long Id, long StartTime, long EndTime)[] expectedCurrentDrawTimeLimitControls =
            [
                (54, 1782370800, 1783580340),
                (55, 1783580400, 1784789940),
                (47101, 1780358400, 1784242800),
                (47201, 1780358400, 1784242800),
                (47703, 1780358400, 1784242800),
                (47704, 1780358400, 1784242800),
                (47911, 1780653600, 1784242800),
                (47912, 1780358400, 1784242800),
                (47913, 1780653600, 1784242800),
                (47920, 1780358400, 1784242800),
                (47921, 1780358400, 1784242800),
                (47922, 1780358400, 1784242800)
            ];
            NotifyLogin login = new()
            {
                FubenMainLine2Data = new()
                {
                    StageDataList = [],
                    ChapterDataList = [],
                    TreasureData = [],
                    AchievementData = [],
                    EggData = [],
                    PassStageIds = []
                },
                FashionColorData = new()
                {
                    FasionColors = []
                },
                TimeLimitCtrlConfigList = expectedCurrentDrawTimeLimitControls
                    .Select(timeLimit => new TimeLimitCtrlConfigList
                    {
                        Id = timeLimit.Id,
                        StartTime = timeLimit.StartTime,
                        EndTime = timeLimit.EndTime
                    })
                    .ToList()
            };

            NotifyLogin roundTrip = MessagePackSerializer.Deserialize<NotifyLogin>(MessagePackSerializer.Serialize(login));

            AssertTimeLimitControlsEqual(
                expectedCurrentDrawTimeLimitControls,
                roundTrip.TimeLimitCtrlConfigList,
                "NotifyLogin TimeLimitCtrlConfigList current draw/event MessagePack round-trip");

            Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            MethodInfo buildNotifyLogin = RequiredMethod(
                accountModule,
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            NotifyLogin productionLogin;
            const long notifyLoginPlayerId = 880007;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(notifyLoginPlayerId),
                CreateDrawCompatibilityPlayer(notifyLoginPlayerId),
                CreateDrawCompatibilityInventory(notifyLoginPlayerId, []),
                "notify-login-time-limit-compat-test"))
            {
                productionLogin = buildNotifyLogin.Invoke(null, [harness.Session]) as NotifyLogin
                    ?? throw new InvalidDataException("AccountModule.BuildNotifyLogin returned nil or a non-NotifyLogin payload.");
            }
            AssertTimeLimitControlIdsPresent(
                expectedCurrentDrawTimeLimitControls.Select(timeLimit => timeLimit.Id).ToArray(),
                productionLogin.TimeLimitCtrlConfigList,
                "AccountModule.BuildNotifyLogin NotifyLogin.TimeLimitCtrlConfigList current draw/event controls");

            NotifyFashionColorData fashionColorData = roundTrip.FashionColorData
                ?? throw new InvalidDataException("NotifyLogin FashionColorData serialized as nil.");
            AssertEmptyList(fashionColorData.FasionColors, "NotifyLogin FashionColorData.FasionColors");

            FubenMainLine2Data fubenMainLine2Data = roundTrip.FubenMainLine2Data
                ?? throw new InvalidDataException("NotifyLogin FubenMainLine2Data serialized as nil.");
            AssertEmptyList(fubenMainLine2Data.StageDataList, "NotifyLogin FubenMainLine2Data.StageDataList");
            AssertEmptyList(fubenMainLine2Data.ChapterDataList, "NotifyLogin FubenMainLine2Data.ChapterDataList");
            AssertEmptyList(fubenMainLine2Data.TreasureData, "NotifyLogin FubenMainLine2Data.TreasureData");
            AssertEmptyList(fubenMainLine2Data.AchievementData, "NotifyLogin FubenMainLine2Data.AchievementData");
            AssertEmptyList(fubenMainLine2Data.EggData, "NotifyLogin FubenMainLine2Data.EggData");
            AssertEmptyList(fubenMainLine2Data.PassStageIds, "NotifyLogin FubenMainLine2Data.PassStageIds");

            static void AssertTimeLimitControlsEqual(
                IReadOnlyList<(long Id, long StartTime, long EndTime)> expected,
                IReadOnlyList<TimeLimitCtrlConfigList>? actual,
                string name)
            {
                if (actual is null)
                    throw new InvalidDataException($"{name}: expected a non-empty list, got nil.");
                if (actual.Count == 0)
                    throw new InvalidDataException($"{name}: expected current draw/event controls, got an empty list.");

                AssertEqual(expected.Count, actual.Count, $"{name} count");
                for (int index = 0; index < expected.Count; index++)
                {
                    AssertEqual(expected[index].Id, actual[index].Id, $"{name}[{index}].Id");
                    AssertEqual(expected[index].StartTime, actual[index].StartTime, $"{name}[{index}].StartTime");
                    AssertEqual(expected[index].EndTime, actual[index].EndTime, $"{name}[{index}].EndTime");
                }
            }

            static void AssertTimeLimitControlIdsPresent(
                IReadOnlyList<long> expectedIds,
                IReadOnlyList<TimeLimitCtrlConfigList>? actual,
                string name)
            {
                if (actual is null)
                    throw new InvalidDataException($"{name}: expected a non-empty list, got nil.");
                if (actual.Count == 0)
                    throw new InvalidDataException($"{name}: expected current draw/event controls, got an empty list.");

                HashSet<long> actualIds = actual.Select(timeLimit => timeLimit.Id).ToHashSet();
                foreach (long expectedId in expectedIds)
                {
                    if (!actualIds.Contains(expectedId))
                        throw new InvalidDataException($"{name}: missing control id {expectedId}.");
                }
            }
        }

        private static void ValidateDrawCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDrawCompatibility();
            const int memberTargetGroupId = 1;
            const int weaponResearchGroupId = 2;
            const int targetWeaponResearchGroupId = 4;
            const int themedEventConstructGroupId = 11;
            const int arrivalConstructGroupId = 12;
            const int fateArrivalConstructGroupId = 13;
            const int fateThemedConstructGroupId = 15;
            const int targetUniframeGroupId = 16;
            const int cubTargetGroupId = 22;
            const long drawGroupPlayerId = 880001;
            const long drawProgressPlayerId = 880002;
            const long drawEventSelectionPlayerId = 880006;
            const long drawPityPlayerId = 880007;
            const long buyAssetPlayerId = 880003;
            const int drawGroupPacketId = 8801;
            const int drawInfoPacketId = 8802;
            const int setUseDrawIdPacketId = 8803;
            const int drawCardPacketId = 8804;
            const int buyAssetPacketId = 8805;

            DrawGetDrawGroupListResponse groupResponse;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(drawGroupPlayerId),
                CreateDrawCompatibilityPlayer(drawGroupPlayerId),
                CreateDrawCompatibilityInventory(drawGroupPlayerId, []),
                "draw-group-compat-test"))
            {
                InvokeRegisteredRequestHandler("DrawGetDrawGroupListRequest", harness.Session, drawGroupPacketId, request: null);
                groupResponse = ReadResponsePayload<DrawGetDrawGroupListResponse>(
                    harness,
                    drawGroupPacketId,
                    nameof(DrawGetDrawGroupListResponse),
                    "DrawGetDrawGroupListRequest response");
            }

            AssertEqual(0, groupResponse.Code, "DrawGetDrawGroupListResponse Code");
            MemberInfo tenDrawOnSalesMember = RequiredDataMember(typeof(DrawGroupInfo), nameof(DrawGroupInfo.TenDrawOnSales));
            AssertEqual(typeof(Dictionary<int, int>), MemberValueType(tenDrawOnSalesMember), "DrawGroupInfo TenDrawOnSales retail MessagePack map type");
            foreach (DrawGroupInfo group in groupResponse.DrawGroupInfoList)
            {
                if (GetRequiredMemberValue(group, tenDrawOnSalesMember) is not Dictionary<int, int> tenDrawOnSales)
                    throw new InvalidDataException($"DrawGetDrawGroupListResponse group {group.Id} TenDrawOnSales: expected a retail map payload.");
                AssertEqual(0, tenDrawOnSales.Count, $"DrawGetDrawGroupListResponse group {group.Id} TenDrawOnSales retail empty map");
            }

            int[] expectedRetailGroupIds =
            [
                memberTargetGroupId,
                weaponResearchGroupId,
                targetWeaponResearchGroupId,
                themedEventConstructGroupId,
                arrivalConstructGroupId,
                fateArrivalConstructGroupId,
                fateThemedConstructGroupId,
                targetUniframeGroupId,
                cubTargetGroupId
            ];
            int[] groupIds = groupResponse.DrawGroupInfoList.Select(group => group.Id).ToArray();
            AssertIntegerList(
                expectedRetailGroupIds.Select(groupId => (long)groupId).ToArray(),
                groupIds.Select(groupId => (long)groupId).ToArray(),
                "DrawGetDrawGroupListResponse visible retail group ids in response order");

            Dictionary<int, DrawGroupInfo> groupById = groupResponse.DrawGroupInfoList.ToDictionary(group => group.Id);
            (int GroupId, long StartTime, long EndTime)[] expectedGroupTimeWindows =
            [
                (targetWeaponResearchGroupId, 0, 0),
                (themedEventConstructGroupId, 1780358400, 1784242800),
                (arrivalConstructGroupId, 1575540000, 1784789940),
                (fateArrivalConstructGroupId, 1575540000, 1784789940),
                (fateThemedConstructGroupId, 1780358400, 1784242800),
                (targetUniframeGroupId, 0, 0),
                (cubTargetGroupId, 0, 0)
            ];
            foreach ((int groupId, long startTime, long endTime) in expectedGroupTimeWindows)
            {
                DrawGroupInfo group = groupById[groupId];
                AssertEqual(startTime, group.StartTime, $"DrawGetDrawGroupListResponse group {groupId} StartTime");
                AssertEqual(endTime, group.EndTime, $"DrawGetDrawGroupListResponse group {groupId} EndTime");
            }

            DrawGroupInfo memberTargetGroup = groupById[memberTargetGroupId];
            AssertIntegerList(
                [101, 3000, 3001, 3002, 3003, 3004, 3005, 3006, 3007, 3008, 3010, 3012, 3014, 3016, 3018, 3020, 3022, 3024, 3026, 3028, 3030, 3032, 3034, 3036],
                memberTargetGroup.OptionalDrawIdList.Select(drawId => (long)drawId).ToArray(),
                "DrawGetDrawGroupListResponse group 1 OptionalDrawIdList");
            AssertIntegerDictionary(
                new Dictionary<int, int> { [0] = 101 },
                memberTargetGroup.UseDrawIdDict,
                "DrawGetDrawGroupListResponse group 1 default UseDrawIdDict");
            DrawGroupInfo targetWeaponGroup = groupById[targetWeaponResearchGroupId];
            AssertIntegerList(
                [301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 311, 312, 313, 314, 315, 316, 317, 318, 319, 320, 321, 322, 323, 324, 325, 326, 327, 328, 329, 330, 331, 332, 333, 334, 335, 336, 337, 338, 339, 340, 341, 342, 343, 344, 345, 346, 347, 348, 349, 350, 351, 353, 354, 355, 356, 357, 358, 359, 360, 361, 362, 363, 364, 365, 366, 367, 370, 371, 372, 374, 375, 376, 377, 378, 379],
                targetWeaponGroup.OptionalDrawIdList.Select(drawId => (long)drawId).ToArray(),
                "DrawGetDrawGroupListResponse group 4 OptionalDrawIdList");
            AssertIntegerDictionary(
                new Dictionary<int, int> { [0] = 371, [3] = 378 },
                targetWeaponGroup.UseDrawIdDict,
                "DrawGetDrawGroupListResponse group 4 default UseDrawIdDict");
            AssertIntegerList([1488, 1498], groupById[themedEventConstructGroupId].OptionalDrawIdList.Select(drawId => (long)drawId).ToArray(), "DrawGetDrawGroupListResponse group 11 OptionalDrawIdList");
            AssertIntegerDictionary(new Dictionary<int, int> { [0] = 0, [5] = 1488 }, groupById[themedEventConstructGroupId].UseDrawIdDict, "DrawGetDrawGroupListResponse group 11 default UseDrawIdDict");
            AssertIntegerList([1492, 1493, 1494], groupById[arrivalConstructGroupId].OptionalDrawIdList.Select(drawId => (long)drawId).ToArray(), "DrawGetDrawGroupListResponse group 12 OptionalDrawIdList");
            AssertIntegerDictionary(new Dictionary<int, int> { [0] = 1494 }, groupById[arrivalConstructGroupId].UseDrawIdDict, "DrawGetDrawGroupListResponse group 12 default UseDrawIdDict");
            AssertIntegerList([2486, 2487, 2488], groupById[fateArrivalConstructGroupId].OptionalDrawIdList.Select(drawId => (long)drawId).ToArray(), "DrawGetDrawGroupListResponse group 13 OptionalDrawIdList");
            AssertIntegerDictionary(new Dictionary<int, int> { [0] = 2487 }, groupById[fateArrivalConstructGroupId].UseDrawIdDict, "DrawGetDrawGroupListResponse group 13 default UseDrawIdDict");
            AssertIntegerList([2482, 2492], groupById[fateThemedConstructGroupId].OptionalDrawIdList.Select(drawId => (long)drawId).ToArray(), "DrawGetDrawGroupListResponse group 15 OptionalDrawIdList");
            AssertIntegerDictionary(new Dictionary<int, int> { [0] = 0 }, groupById[fateThemedConstructGroupId].UseDrawIdDict, "DrawGetDrawGroupListResponse group 15 default UseDrawIdDict");
            AssertIntegerList([4001, 4003, 4005, 4007, 4009, 4011, 4013], groupById[targetUniframeGroupId].OptionalDrawIdList.Select(drawId => (long)drawId).ToArray(), "DrawGetDrawGroupListResponse group 16 OptionalDrawIdList");
            AssertIntegerDictionary(new Dictionary<int, int> { [0] = 4003 }, groupById[targetUniframeGroupId].UseDrawIdDict, "DrawGetDrawGroupListResponse group 16 default UseDrawIdDict");
            AssertIntegerList(
                [7002, 7004, 7006, 7008, 7010, 7012, 7014, 7016, 7018, 7020, 7022, 7024, 7026, 7028, 7030, 7032, 7034, 7036, 7038, 7040, 7042, 7044, 7046, 7048, 7052, 7054, 7057, 7059, 7061, 7063, 7064, 7065],
                groupById[cubTargetGroupId].OptionalDrawIdList.Select(drawId => (long)drawId).ToArray(),
                "DrawGetDrawGroupListResponse group 22 OptionalDrawIdList");
            AssertIntegerDictionary(
                new Dictionary<int, int> { [0] = 7059, [4] = 7065 },
                groupById[cubTargetGroupId].UseDrawIdDict,
                "DrawGetDrawGroupListResponse group 22 default UseDrawIdDict");

            AssertEqual(1, groupResponse.DrawAdjustActivityInfoList.Count, "DrawGetDrawGroupListResponse DrawAdjustActivityInfoList count");
            DrawAdjustActivityInfo adjustActivity = groupResponse.DrawAdjustActivityInfoList.Single();
            AssertEqual(memberTargetGroupId, adjustActivity.DrawGroupId, "DrawGetDrawGroupListResponse DrawAdjustActivityInfo DrawGroupId");
            AssertEqual(1241003, adjustActivity.TargetId, "DrawGetDrawGroupListResponse DrawAdjustActivityInfo TargetId");
            AssertEqual(3, adjustActivity.ActivityId, "DrawGetDrawGroupListResponse DrawAdjustActivityInfo ActivityId");
            AssertEqual(1763006400L, adjustActivity.StartTime, "DrawGetDrawGroupListResponse DrawAdjustActivityInfo StartTime");
            AssertEqual(1, adjustActivity.AdjustTimes, "DrawGetDrawGroupListResponse DrawAdjustActivityInfo AdjustTimes");
            AssertIntegerList(
                [1011003, 1031003, 1061003, 1071003, 1051003, 1021003, 1041003, 1021004, 1141003, 1171003, 1121003, 1131003, 1031004, 1531004, 1051004, 1071004, 1041004, 1011004, 1091003, 1021005, 1221003, 1261003, 1271003, 1081004, 1521004, 1171004, 1241003, 1211003, 1021006, 1051005, 1321003, 1331003, 1531005, 1131004, 1041005, 1381003, 1291003, 1141004, 1391003],
                adjustActivity.EffectTargetTemplateIds.Select(templateId => (long)templateId).ToArray(),
                "DrawGetDrawGroupListResponse DrawAdjustActivityInfo EffectTargetTemplateIds");

            DrawGetDrawInfoListRequest infoRequest = new()
            {
                GroupId = targetWeaponResearchGroupId
            };
            DrawGetDrawInfoListRequest infoRequestRoundTrip = MessagePackSerializer.Deserialize<DrawGetDrawInfoListRequest>(
                MessagePackSerializer.Serialize(infoRequest));
            AssertEqual(targetWeaponResearchGroupId, infoRequestRoundTrip.GroupId, "DrawGetDrawInfoListRequest GroupId MessagePack round-trip");

            DrawGetDrawInfoListResponse infoResponse;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(drawGroupPlayerId),
                CreateDrawCompatibilityPlayer(drawGroupPlayerId),
                CreateDrawCompatibilityInventory(drawGroupPlayerId, []),
                "draw-info-compat-test"))
            {
                InvokeRegisteredRequestHandler("DrawGetDrawInfoListRequest", harness.Session, drawInfoPacketId, infoRequestRoundTrip);
                infoResponse = ReadResponsePayload<DrawGetDrawInfoListResponse>(
                    harness,
                    drawInfoPacketId,
                    nameof(DrawGetDrawInfoListResponse),
                    "DrawGetDrawInfoListRequest response");
            }

            AssertEqual(0, infoResponse.Code, "DrawGetDrawInfoListResponse Code");
            AssertEqual(75, infoResponse.DrawInfoList.Count, "DrawGetDrawInfoListResponse group 4 retail draw info count");
            AssertCurrentWeaponBannerTargetEquipRows(
                new Dictionary<int, int>
                {
                    [370] = 2576001,
                    [371] = 2586001,
                    [372] = 2596001,
                    [374] = 2616001,
                    [375] = 2626001,
                    [376] = 2636001,
                    [377] = 2646001,
                    [378] = 2656001,
                    [379] = 2606001
                },
                infoResponse.DrawInfoList);
            const string targetWeaponPowerBanner = "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1WeaponPower.prefab";
            AssertRetailDrawInfoArt(
                infoResponse.DrawInfoList.Single(info => info.Id == 378),
                378,
                targetWeaponResearchGroupId,
                targetWeaponPowerBanner,
                new Dictionary<int, string>(),
                new Dictionary<int, int> { [1] = 2656001 },
                [5, 6],
                3,
                "DrawGetDrawInfoListResponse target weapon 378");
            AssertRetailDrawInfoArt(
                infoResponse.DrawInfoList.Single(info => info.Id == 379),
                379,
                targetWeaponResearchGroupId,
                targetWeaponPowerBanner,
                new Dictionary<int, string>(),
                new Dictionary<int, int> { [1] = 2606001 },
                [5, 6],
                3,
                "DrawGetDrawInfoListResponse target weapon 379");

            DrawGetDrawInfoListResponse memberTargetInfoResponse = ReadDrawInfoListForGroup(memberTargetGroupId, drawInfoPacketId + 9, drawGroupPlayerId, "draw-member-target-info-compat-test");
            AssertEqual(0, memberTargetInfoResponse.Code, "DrawGetDrawInfoListResponse group 1 Code");

            DrawGetDrawInfoListResponse themedEventInfoResponse = ReadDrawInfoListForGroup(themedEventConstructGroupId, drawInfoPacketId + 10, drawGroupPlayerId, "draw-themed-event-info-compat-test");
            AssertEqual(0, themedEventInfoResponse.Code, "DrawGetDrawInfoListResponse group 11 Code");
            DrawInfo eventConstruct1488 = themedEventInfoResponse.DrawInfoList.Single(info => info.Id == 1488);
            AssertRetailDrawInfoArt(
                eventConstruct1488,
                1488,
                themedEventConstructGroupId,
                "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5Power02.prefab",
                new Dictionary<int, string>(),
                new Dictionary<int, int> { [1] = 1021007 },
                [5, 6, 2],
                5,
                "DrawGetDrawInfoListResponse event construct 1488");
            AssertDraw1488RetailRewardPool(eventConstruct1488, drawPityPlayerId + 1, weaponResearchGroupId, targetWeaponResearchGroupId, cubTargetGroupId);
            AssertRetailDrawInfoArt(
                themedEventInfoResponse.DrawInfoList.Single(info => info.Id == 1498),
                1498,
                themedEventConstructGroupId,
                "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5Power01.prefab",
                new Dictionary<int, string>(),
                new Dictionary<int, int> { [1] = 1031005 },
                [5, 6, 2],
                1,
                "DrawGetDrawInfoListResponse event construct 1498");

            DrawGetDrawInfoListResponse fateEventInfoResponse = ReadDrawInfoListForGroup(fateThemedConstructGroupId, drawInfoPacketId + 11, drawGroupPlayerId, "draw-fate-event-info-compat-test");
            AssertEqual(0, fateEventInfoResponse.Code, "DrawGetDrawInfoListResponse group 15 Code");
            AssertRetailDrawInfoArt(
                fateEventInfoResponse.DrawInfoList.Single(info => info.Id == 2482),
                2482,
                fateThemedConstructGroupId,
                "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5Power02.prefab",
                new Dictionary<int, string>(),
                new Dictionary<int, int> { [1] = 1021007 },
                [5, 6, 2],
                6,
                "DrawGetDrawInfoListResponse fate event 2482");
            AssertRetailDrawInfoArt(
                fateEventInfoResponse.DrawInfoList.Single(info => info.Id == 2492),
                2492,
                fateThemedConstructGroupId,
                "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5Power01.prefab",
                new Dictionary<int, string>(),
                new Dictionary<int, int> { [1] = 1031005 },
                [5, 6, 2],
                2,
                "DrawGetDrawInfoListResponse fate event 2492");

            DrawGetDrawInfoListResponse arrivalInfoResponse = ReadDrawInfoListForGroup(arrivalConstructGroupId, drawInfoPacketId + 13, drawGroupPlayerId, "draw-arrival-info-compat-test");
            AssertEqual(0, arrivalInfoResponse.Code, "DrawGetDrawInfoListResponse group 12 Code");
            DrawGetDrawInfoListResponse fateArrivalInfoResponse = ReadDrawInfoListForGroup(fateArrivalConstructGroupId, drawInfoPacketId + 14, drawGroupPlayerId, "draw-fate-arrival-info-compat-test");
            AssertEqual(0, fateArrivalInfoResponse.Code, "DrawGetDrawInfoListResponse group 13 Code");
            AssertCurrentVersionJumpDrawTargetRows(
                [
                    (3024, 1241002),
                    (1488, 1021007),
                    (1498, 1031005),
                    (1492, 1291003),
                    (1493, 1381003),
                    (1494, 1171004),
                    (2482, 1021007),
                    (2492, 1031005),
                    (2486, 1291003),
                    (2487, 1381003),
                    (2488, 1171004)
                ],
                [
                    memberTargetInfoResponse.DrawInfoList.Single(info => info.Id == 3024),
                    eventConstruct1488,
                    themedEventInfoResponse.DrawInfoList.Single(info => info.Id == 1498),
                    arrivalInfoResponse.DrawInfoList.Single(info => info.Id == 1492),
                    arrivalInfoResponse.DrawInfoList.Single(info => info.Id == 1493),
                    arrivalInfoResponse.DrawInfoList.Single(info => info.Id == 1494),
                    fateEventInfoResponse.DrawInfoList.Single(info => info.Id == 2482),
                    fateEventInfoResponse.DrawInfoList.Single(info => info.Id == 2492),
                    fateArrivalInfoResponse.DrawInfoList.Single(info => info.Id == 2486),
                    fateArrivalInfoResponse.DrawInfoList.Single(info => info.Id == 2487),
                    fateArrivalInfoResponse.DrawInfoList.Single(info => info.Id == 2488)
                ]);
            AssertCurrentDrawTargetCharacterPersistenceRows(
                [
                    1221003,
                    1261003,
                    1271003,
                    1081004,
                    1521004,
                    1171004,
                    1241003,
                    1211003,
                    1021006,
                    1051005,
                    1321003,
                    1331003,
                    1531005,
                    1131004,
                    1041005,
                    1141004,
                    1391003,
                    1341003,
                    1061004
                ]);
            AssertCurrentCharacterGradeTablesAndPromotionBehavior();

            DrawGetDrawInfoListResponse cubInfoResponse = ReadDrawInfoListForGroup(cubTargetGroupId, drawInfoPacketId + 12, drawGroupPlayerId, "draw-cub-info-compat-test");
            AssertEqual(0, cubInfoResponse.Code, "DrawGetDrawInfoListResponse group 22 Code");
            const string cubPowerBanner = "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaborationV1CubPower.prefab";
            const string cubPowerResource = "Assets/Product/Texture/Image/DrawCollaborationName/UiDrawCollaborationV1/UiDrawCollaborationV1CUB3.png";
            AssertRetailDrawInfoArt(
                cubInfoResponse.DrawInfoList.Single(info => info.Id == 7064),
                7064,
                cubTargetGroupId,
                cubPowerBanner,
                new Dictionary<int, string> { [4] = cubPowerResource },
                new Dictionary<int, int> { [1] = 16390000 },
                [5, 6],
                4,
                "DrawGetDrawInfoListResponse CUB 7064");
            AssertRetailDrawInfoArt(
                cubInfoResponse.DrawInfoList.Single(info => info.Id == 7065),
                7065,
                cubTargetGroupId,
                cubPowerBanner,
                new Dictionary<int, string> { [4] = cubPowerResource },
                new Dictionary<int, int> { [1] = 16340000 },
                [5, 6],
                4,
                "DrawGetDrawInfoListResponse CUB 7065");

            const int selectedRetailDrawId = 379;
            DrawSetUseDrawIdRequest setUseDrawIdRequest = new()
            {
                DrawId = selectedRetailDrawId
            };
            DrawSetUseDrawIdRequest setUseDrawIdRequestRoundTrip = MessagePackSerializer.Deserialize<DrawSetUseDrawIdRequest>(
                MessagePackSerializer.Serialize(setUseDrawIdRequest));
            AssertEqual(selectedRetailDrawId, setUseDrawIdRequestRoundTrip.DrawId, "DrawSetUseDrawIdRequest DrawId MessagePack round-trip");

            DrawSetUseDrawIdResponse setUseDrawIdResponse;
            DrawGetDrawGroupListResponse selectedGroupResponse;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(drawProgressPlayerId),
                CreateDrawCompatibilityPlayer(drawProgressPlayerId),
                CreateDrawCompatibilityInventory(drawProgressPlayerId, []),
                "draw-selection-compat-test"))
            {
                InvokeRegisteredRequestHandler("DrawSetUseDrawIdRequest", harness.Session, setUseDrawIdPacketId, setUseDrawIdRequestRoundTrip);
                setUseDrawIdResponse = ReadResponsePayload<DrawSetUseDrawIdResponse>(
                    harness,
                    setUseDrawIdPacketId,
                    nameof(DrawSetUseDrawIdResponse),
                    "DrawSetUseDrawIdRequest response");
                InvokeRegisteredRequestHandler("DrawGetDrawGroupListRequest", harness.Session, drawGroupPacketId + 1, request: null);
                selectedGroupResponse = ReadResponsePayload<DrawGetDrawGroupListResponse>(
                    harness,
                    drawGroupPacketId + 1,
                    nameof(DrawGetDrawGroupListResponse),
                    "DrawSetUseDrawIdRequest follow-up group response");
            }

            AssertEqual(0, setUseDrawIdResponse.Code, "DrawSetUseDrawIdResponse Code");
            AssertEqual(1, setUseDrawIdResponse.SwitchDrawIdCount, "DrawSetUseDrawIdResponse SwitchDrawIdCount");
            DrawGroupInfo selectedGroup = selectedGroupResponse.DrawGroupInfoList.Single(group => group.Id == targetWeaponResearchGroupId);
            AssertEqual(1, selectedGroup.SwitchDrawIdCount, "DrawGetDrawGroupListResponse selected group SwitchDrawIdCount after selection");
            AssertIntegerDictionary(
                new Dictionary<int, int> { [0] = 371, [3] = selectedRetailDrawId },
                selectedGroup.UseDrawIdDict,
                "DrawGetDrawGroupListResponse selected group UseDrawIdDict after selecting 379");

            const int selectedFateEventDrawId = 2482;
            DrawSetUseDrawIdRequest setFateEventDrawIdRequest = new()
            {
                DrawId = selectedFateEventDrawId
            };
            DrawSetUseDrawIdRequest setFateEventDrawIdRequestRoundTrip = MessagePackSerializer.Deserialize<DrawSetUseDrawIdRequest>(
                MessagePackSerializer.Serialize(setFateEventDrawIdRequest));
            AssertEqual(selectedFateEventDrawId, setFateEventDrawIdRequestRoundTrip.DrawId, "DrawSetUseDrawIdRequest fate event DrawId MessagePack round-trip");

            DrawSetUseDrawIdResponse setFateEventDrawIdResponse;
            DrawGetDrawGroupListResponse selectedFateEventGroupResponse;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(drawEventSelectionPlayerId),
                CreateDrawCompatibilityPlayer(drawEventSelectionPlayerId),
                CreateDrawCompatibilityInventory(drawEventSelectionPlayerId, []),
                "draw-event-selection-compat-test"))
            {
                InvokeRegisteredRequestHandler("DrawSetUseDrawIdRequest", harness.Session, setUseDrawIdPacketId + 1, setFateEventDrawIdRequestRoundTrip);
                setFateEventDrawIdResponse = ReadResponsePayload<DrawSetUseDrawIdResponse>(
                    harness,
                    setUseDrawIdPacketId + 1,
                    nameof(DrawSetUseDrawIdResponse),
                    "DrawSetUseDrawIdRequest fate event response");
                InvokeRegisteredRequestHandler("DrawGetDrawGroupListRequest", harness.Session, drawGroupPacketId + 2, request: null);
                selectedFateEventGroupResponse = ReadResponsePayload<DrawGetDrawGroupListResponse>(
                    harness,
                    drawGroupPacketId + 2,
                    nameof(DrawGetDrawGroupListResponse),
                    "DrawSetUseDrawIdRequest fate event follow-up group response");
            }

            AssertEqual(0, setFateEventDrawIdResponse.Code, "DrawSetUseDrawIdResponse fate event Code");
            AssertEqual(1, setFateEventDrawIdResponse.SwitchDrawIdCount, "DrawSetUseDrawIdResponse fate event SwitchDrawIdCount");
            DrawGroupInfo selectedFateEventGroup = selectedFateEventGroupResponse.DrawGroupInfoList.Single(group => group.Id == fateThemedConstructGroupId);
            AssertEqual(1, selectedFateEventGroup.SwitchDrawIdCount, "DrawGetDrawGroupListResponse fate event group SwitchDrawIdCount after selection");
            AssertIntegerDictionary(
                new Dictionary<int, int> { [0] = 0, [5] = selectedFateEventDrawId },
                selectedFateEventGroup.UseDrawIdDict,
                "DrawGetDrawGroupListResponse group 15 UseDrawIdDict after selecting 2482 into slot 5");

            const int rewardBackedDrawId = 302;
            DrawInfo drawInfoBeforeCard = infoResponse.DrawInfoList.First(info => info.Id == rewardBackedDrawId);
            int initialDrawTicketCount = drawInfoBeforeCard.UseItemCount * 4;
            DrawDrawCardRequest drawCardRequest = new()
            {
                DrawId = rewardBackedDrawId,
                Count = 1,
                UseDrawTicketId = 0
            };
            DrawDrawCardRequest drawCardRequestRoundTrip = MessagePackSerializer.Deserialize<DrawDrawCardRequest>(
                MessagePackSerializer.Serialize(drawCardRequest));
            AssertEqual(rewardBackedDrawId, drawCardRequestRoundTrip.DrawId, "DrawDrawCardRequest DrawId MessagePack round-trip");
            AssertEqual(1, drawCardRequestRoundTrip.Count, "DrawDrawCardRequest Count MessagePack round-trip");
            AssertEqual(0, drawCardRequestRoundTrip.UseDrawTicketId, "DrawDrawCardRequest UseDrawTicketId MessagePack round-trip");
            AssertDrawDrawCardHandlerUsesPlayerScopedPullOffset();

            DrawDrawCardResponse drawCardResponse = null!;
            NotifyItemDataList drawItemPush = null!;
            NotifyEquipDataList? drawEquipPush = null;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(drawProgressPlayerId),
                CreateDrawCompatibilityPlayer(drawProgressPlayerId),
                CreateDrawCompatibilityInventory(drawProgressPlayerId, [new Item { Id = drawInfoBeforeCard.UseItemId, Count = initialDrawTicketCount }]),
                "draw-card-compat-test"))
            {
                InvokeRegisteredRequestHandler("DrawDrawCardRequest", harness.Session, drawCardPacketId, drawCardRequestRoundTrip);
                Packet firstPacket = harness.ReadPacket("DrawDrawCardRequest first packet");
                AssertEqual(Packet.ContentType.Push, firstPacket.Type, "DrawDrawCardRequest first packet type");
                Packet.Push firstPush = MessagePackSerializer.Deserialize<Packet.Push>(firstPacket.Content);
                AssertEqual(nameof(NotifyItemDataList), firstPush.Name, "DrawDrawCardRequest first push");
                drawItemPush = MessagePackSerializer.Deserialize<NotifyItemDataList>(firstPush.Content);

                for (int packetIndex = 0; packetIndex < 4; packetIndex++)
                {
                    Packet packet = harness.ReadPacket($"DrawDrawCardRequest packet {packetIndex + 2}");
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        if (push.Name == nameof(NotifyEquipDataList))
                            drawEquipPush = MessagePackSerializer.Deserialize<NotifyEquipDataList>(push.Content);
                        continue;
                    }

                    AssertEqual(Packet.ContentType.Response, packet.Type, "DrawDrawCardRequest response packet type");
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(drawCardPacketId, response.Id, "DrawDrawCardRequest response packet id");
                    AssertEqual(nameof(DrawDrawCardResponse), response.Name, "DrawDrawCardRequest response packet name");
                    drawCardResponse = MessagePackSerializer.Deserialize<DrawDrawCardResponse>(response.Content);
                    goto FoundDrawCardResponse;
                }

                throw new InvalidDataException("DrawDrawCardRequest: expected DrawDrawCardResponse after inventory pushes.");

            FoundDrawCardResponse:
                ;
            }

            Item costItem = drawItemPush.ItemDataList.Single(item => item.Id == drawInfoBeforeCard.UseItemId);
            AssertEqual((long)(initialDrawTicketCount - drawInfoBeforeCard.UseItemCount), costItem.Count, "DrawDrawCardRequest NotifyItemDataList consumed draw ticket count");
            AssertEqual(1, drawCardResponse.RewardGoodsList.Count, "DrawDrawCardResponse RewardGoodsList count for 1x draw");
            RewardGoods reward = drawCardResponse.RewardGoodsList[0];
            AssertDrawRewardPushMatchesRewardGoods(drawInfoBeforeCard, reward, drawItemPush, drawEquipPush, "DrawDrawCardRequest reward notification");
            AssertRequiredMemberNull(drawCardResponse, nameof(DrawDrawCardResponse.ExtraRewardList), "DrawDrawCardResponse ExtraRewardList nullable protocol field");
            AssertRequiredMemberNull(drawCardResponse, nameof(DrawDrawCardResponse.DrawAdjustData), "DrawDrawCardResponse DrawAdjustData nullable protocol field");
            DrawInfo clientDrawInfo = drawCardResponse.ClientDrawInfo
                ?? throw new InvalidDataException("DrawDrawCardResponse ClientDrawInfo: expected draw progress payload.");
            AssertEqual(rewardBackedDrawId, clientDrawInfo.Id, "DrawDrawCardResponse ClientDrawInfo Id");
            AssertEqual(drawInfoBeforeCard.TotalCount + 1, clientDrawInfo.TotalCount, "DrawDrawCardResponse ClientDrawInfo TotalCount after 1x draw");
            AssertEqual(drawInfoBeforeCard.TodayCount + 1, clientDrawInfo.TodayCount, "DrawDrawCardResponse ClientDrawInfo TodayCount after 1x draw");
            AssertEqual(drawInfoBeforeCard.BottomTimes - 1, clientDrawInfo.BottomTimes, "DrawDrawCardResponse ClientDrawInfo BottomTimes after 1x draw");
            AssertTargetWeaponPityDraw(drawPityPlayerId, drawInfoBeforeCard);

            const int consumeItemId = AscNet.Common.Database.Inventory.FreeGem;
            const int targetDrawTicketItemId = 50005;
            const int initialConsumeCount = 30;
            const int initialTargetTicketCount = 7;
            const int buyTimes = 3;
            ItemBuyAssetRequest buyAssetRequest = new()
            {
                Times = buyTimes,
                ItemId = targetDrawTicketItemId,
                ConsumeId = consumeItemId
            };
            ItemBuyAssetRequest buyAssetRequestRoundTrip = MessagePackSerializer.Deserialize<ItemBuyAssetRequest>(
                MessagePackSerializer.Serialize(buyAssetRequest));
            AssertEqual(buyTimes, buyAssetRequestRoundTrip.Times, "ItemBuyAssetRequest Times MessagePack round-trip");
            AssertEqual(targetDrawTicketItemId, buyAssetRequestRoundTrip.ItemId, "ItemBuyAssetRequest ItemId MessagePack round-trip");
            AssertEqual(consumeItemId, buyAssetRequestRoundTrip.ConsumeId, "ItemBuyAssetRequest ConsumeId MessagePack round-trip");

            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(buyAssetPlayerId),
                CreateDrawCompatibilityPlayer(buyAssetPlayerId),
                CreateDrawCompatibilityInventory(
                    buyAssetPlayerId,
                    [
                        new Item { Id = consumeItemId, Count = initialConsumeCount },
                        new Item { Id = targetDrawTicketItemId, Count = initialTargetTicketCount }
                    ]),
                "item-buy-asset-compat-test"))
            {
                InvokeRegisteredRequestHandler("ItemBuyAssetRequest", harness.Session, buyAssetPacketId, buyAssetRequestRoundTrip);
                Packet pushPacket = harness.ReadPacket("ItemBuyAssetRequest NotifyItemDataList push");
                AssertEqual(Packet.ContentType.Push, pushPacket.Type, "ItemBuyAssetRequest first packet type");
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(pushPacket.Content);
                AssertEqual(nameof(NotifyItemDataList), push.Name, "ItemBuyAssetRequest first push");
                NotifyItemDataList notifyItemDataList = MessagePackSerializer.Deserialize<NotifyItemDataList>(push.Content);
                Item consumedItem = notifyItemDataList.ItemDataList.Single(item => item.Id == consumeItemId);
                Item boughtItem = notifyItemDataList.ItemDataList.Single(item => item.Id == targetDrawTicketItemId);
                AssertEqual((long)(initialConsumeCount - buyTimes), consumedItem.Count, "ItemBuyAssetRequest NotifyItemDataList consumed item count");
                AssertEqual((long)(initialTargetTicketCount + buyTimes), boughtItem.Count, "ItemBuyAssetRequest NotifyItemDataList target draw ticket count");

                ItemBuyAssetResponse buyAssetResponse = ReadResponsePayload<ItemBuyAssetResponse>(
                    harness,
                    buyAssetPacketId,
                    nameof(ItemBuyAssetResponse),
                    "ItemBuyAssetRequest response");
                AssertEqual(buyTimes, buyAssetResponse.Count, "ItemBuyAssetResponse Count");
            }
        }

        private static void AssertDrawDrawCardHandlerUsesPlayerScopedPullOffset()
        {
            Type drawManagerType = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
            MethodInfo drawDraw = RequiredMethod(
                drawManagerType,
                "DrawDraw",
                BindingFlags.Static | BindingFlags.Public,
                [typeof(long), typeof(int), typeof(int)]);
            MethodInfo handler = GetRegisteredRequestHandlerMethod("DrawDrawCardRequest");
            List<IlInstruction> instructions = ReadIlInstructions(handler).ToList();
            int drawDrawIndex = FindCallIndex(instructions, drawDraw, startIndex: 0);
            if (drawDrawIndex < 0)
                throw new InvalidDataException("DrawDrawCardRequestHandler draw call: expected handler to call DrawManager.DrawDraw(long playerId, int drawId, int pullOffset).");

            FieldInfo sessionPlayer = typeof(Session).GetField(nameof(Session.player), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(Session).FullName, nameof(Session.player));
            MethodInfo playerDataGetter = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                $"get_{nameof(AscNet.Common.Database.Player.PlayerData)}",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo playerDataIdGetter = RequiredMethod(
                typeof(PlayerData),
                $"get_{nameof(PlayerData.Id)}",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo drawIdGetter = RequiredMethod(
                typeof(DrawDrawCardRequest),
                $"get_{nameof(DrawDrawCardRequest.DrawId)}",
                BindingFlags.Instance | BindingFlags.Public);

            int windowStart = Math.Max(0, drawDrawIndex - 16);
            if (!instructions.Skip(windowStart).Take(drawDrawIndex - windowStart).Any(instruction =>
                    instruction.OpCode == OpCodes.Ldfld
                    && instruction.Operand is FieldInfo loadedField
                    && FieldsMatch(loadedField, sessionPlayer)))
                throw new InvalidDataException("DrawDrawCardRequestHandler draw call: expected player id argument to start from Session.player.");
            if (!instructions.Skip(windowStart).Take(drawDrawIndex - windowStart).Any(instruction =>
                    instruction.Operand is MethodBase calledMethod
                    && MethodsMatch(calledMethod, playerDataGetter)))
                throw new InvalidDataException("DrawDrawCardRequestHandler draw call: expected player id argument to load Player.PlayerData.");
            if (!instructions.Skip(windowStart).Take(drawDrawIndex - windowStart).Any(instruction =>
                    instruction.Operand is MethodBase calledMethod
                    && MethodsMatch(calledMethod, playerDataIdGetter)))
                throw new InvalidDataException("DrawDrawCardRequestHandler draw call: expected player id argument to load PlayerData.Id.");
            if (!instructions.Skip(windowStart).Take(drawDrawIndex - windowStart).Any(instruction =>
                    instruction.Operand is MethodBase calledMethod
                    && MethodsMatch(calledMethod, drawIdGetter)))
                throw new InvalidDataException("DrawDrawCardRequestHandler draw call: expected draw id argument to load DrawDrawCardRequest.DrawId.");
            if (!instructions.Skip(windowStart).Take(drawDrawIndex - windowStart).Any(instruction =>
                    instruction.OpCode == OpCodes.Ldloc || instruction.OpCode == OpCodes.Ldloc_S || instruction.OpCode == OpCodes.Ldloc_0 || instruction.OpCode == OpCodes.Ldloc_1 || instruction.OpCode == OpCodes.Ldloc_2 || instruction.OpCode == OpCodes.Ldloc_3))
                throw new InvalidDataException("DrawDrawCardRequestHandler draw call: expected pull offset argument to load the draw loop index.");
        }

        private static void AssertDraw1488RetailRewardPool(DrawInfo eventConstruct1488, long drawPityPlayerId, int weaponResearchGroupId, int targetWeaponResearchGroupId, int cubTargetGroupId)
        {
            AssertEqual(false, eventConstruct1488.GroupId == weaponResearchGroupId || eventConstruct1488.GroupId == targetWeaponResearchGroupId || eventConstruct1488.GroupId == cubTargetGroupId, "Draw 1488 retail-like reward routing uses construct reward pool");
            AssertTargetCharacterPityDraw(drawPityPlayerId, eventConstruct1488, expectedTargetCharacterId: 1021007, targetName: "Lucia: Inverse Crown");

            List<CharacterTable> characterRows = TableReaderV2.Parse<CharacterTable>();
            List<ItemTable> itemRows = TableReaderV2.Parse<ItemTable>();
            int targetCharacterId = eventConstruct1488.ResourceIds.TryGetValue(1, out int resourceId)
                ? resourceId
                : throw new InvalidDataException("Draw 1488 ResourceIds: expected target character id in slot 1.");
            CharacterTable? targetCharacter = characterRows.SingleOrDefault(character => character.Id == targetCharacterId);
            int[] capturedShardItemIds = [561, 533, 528, 502];
            int[] localShardItemIds = targetCharacter?.ItemId > 0 ? capturedShardItemIds.Append(targetCharacter.ItemId).Distinct().ToArray() : capturedShardItemIds;
            foreach (int shardItemId in localShardItemIds)
            {
                ItemTable shardItem = itemRows.SingleOrDefault(item => item.Id == shardItemId)
                    ?? throw new InvalidDataException($"Draw 1488 shard pool: expected local Inver-Shard item {shardItemId}.");
                if (!shardItem.Name.StartsWith("Inver-Shard", StringComparison.Ordinal))
                    throw new InvalidDataException($"Draw 1488 shard pool item {shardItem.Id}: expected Inver-Shard item name, got '{shardItem.Name}'.");
            }

            Type drawManagerType = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
            Type drawInfoTemplateType = drawManagerType.GetNestedType("DrawInfoTemplate", BindingFlags.NonPublic)
                ?? throw new MissingMemberException(drawManagerType.FullName, "DrawInfoTemplate");
            MethodInfo drawCharacterReward = RequiredMethod(
                drawManagerType,
                "DrawCharacterReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(bool)]);
            MethodInfo drawCharacterShardReward = RequiredMethod(
                drawManagerType,
                "DrawCharacterShardReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType]);
            MethodInfo drawMemoryReward = RequiredMethod(
                drawManagerType,
                "DrawMemoryReward",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo tryCreateTargetCharacterReward = RequiredMethod(
                drawManagerType,
                "TryCreateTargetCharacterReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(RewardGoods).MakeByRefType()]);
            MethodInfo drawRandomCharacterReward = RequiredMethod(
                drawManagerType,
                "DrawRandomCharacterReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(int)]);
            MethodInfo drawOverclockMaterialReward = RequiredMethod(
                drawManagerType,
                "DrawOverclockMaterialReward",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo drawExpMaterialReward = RequiredMethod(
                drawManagerType,
                "DrawExpMaterialReward",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo drawCogBoxReward = RequiredMethod(
                drawManagerType,
                "DrawCogBoxReward",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo drawRandomWeaponReward = RequiredMethod(
                drawManagerType,
                "DrawRandomWeaponReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(int), typeof(int)]);

            List<IlInstruction> drawCharacterRewardInstructions = ReadIlInstructions(drawCharacterReward).ToList();
            (MethodInfo Method, string Category)[] expectedConstructRewardBranches =
            [
                (tryCreateTargetCharacterReward, "target character"),
                (drawRandomCharacterReward, "low-rarity character"),
                (drawCharacterShardReward, "target shard"),
                (drawMemoryReward, "4-star memory"),
                (drawOverclockMaterialReward, "overclock material"),
                (drawExpMaterialReward, "exp material"),
                (drawCogBoxReward, "Cog Pack")
            ];
            foreach ((MethodInfo method, string category) in expectedConstructRewardBranches)
            {
                if (FindCallIndex(drawCharacterRewardInstructions, method, startIndex: 0) < 0)
                    throw new InvalidDataException($"Draw 1488 retail-like reward routing: expected construct reward pool to reach {category} branch.");
            }
            if (FindCallIndex(drawCharacterRewardInstructions, drawRandomWeaponReward, startIndex: 0) >= 0)
                throw new InvalidDataException("Draw 1488 retail-like reward routing: expected construct reward pool not to reach off-rate weapon branch.");
            AssertMethodContainsIntConstants(drawCharacterReward, drawCharacterRewardInstructions, [190, 1585, 3796, 6689, 7831, 8312, 9247], "Draw 1488 retail-like construct reward thresholds");
            List<IlInstruction> shardInstructions = ReadIlInstructions(drawCharacterShardReward).ToList();
            AssertMethodContainsIntConstants(drawCharacterShardReward, shardInstructions, [2, 6, 18], "Draw 1488 Inver-Shard reward counts");

            int[] capturedMemoryEquipIds = [3054001, 3064001, 3054002, 3024001, 3064004, 3024003];
            Dictionary<int, EquipTable> equipRowsById = TableReaderV2.Parse<EquipTable>().ToDictionary(equip => equip.Id);
            foreach (int capturedMemoryEquipId in capturedMemoryEquipIds)
            {
                if (!equipRowsById.TryGetValue(capturedMemoryEquipId, out EquipTable? capturedMemory))
                    throw new InvalidDataException($"Draw 1488 retail-like memory pool: expected local equip row {capturedMemoryEquipId} from retail capture.");
                AssertEqual(0, capturedMemory.Type, $"Draw 1488 retail-like memory pool {capturedMemoryEquipId} Type");
                AssertEqual(4, capturedMemory.Quality, $"Draw 1488 retail-like memory pool {capturedMemoryEquipId} Quality");
                AssertEqual(true, AscNet.Common.Database.Character.IsOwnableEquipTemplate(capturedMemory), $"Draw 1488 retail-like memory pool {capturedMemoryEquipId} ownable equip template");
            }
        }

        private static void AssertCurrentWeaponBannerTargetEquipRows(IReadOnlyDictionary<int, int> expectedTargetEquipIds, IReadOnlyList<DrawInfo> drawInfos)
        {
            Dictionary<int, DrawInfo> drawInfoById = drawInfos.ToDictionary(info => info.Id);
            Dictionary<int, EquipTable> equipRowsById = TableReaderV2.Parse<EquipTable>().ToDictionary(equip => equip.Id);

            foreach (KeyValuePair<int, int> expectedTarget in expectedTargetEquipIds)
            {
                int drawId = expectedTarget.Key;
                int expectedTargetEquipId = expectedTarget.Value;
                if (!drawInfoById.TryGetValue(drawId, out DrawInfo? drawInfo))
                    throw new InvalidDataException($"Current weapon-banner draw {drawId}: expected DrawInfo row.");
                if (!drawInfo.ResourceIds.TryGetValue(1, out int actualTargetEquipId))
                    throw new InvalidDataException($"Current weapon-banner draw {drawId}: expected ResourceIds[1] target equip id.");

                AssertEqual(expectedTargetEquipId, actualTargetEquipId, $"Current weapon-banner draw {drawId} ResourceIds[1]");
                if (!equipRowsById.TryGetValue(actualTargetEquipId, out EquipTable? targetEquip))
                    throw new InvalidDataException($"Current weapon-banner draw {drawId}: expected EquipTable row {actualTargetEquipId}.");
                if (targetEquip.Type <= 0)
                    throw new InvalidDataException($"Current weapon-banner draw {drawId}: expected EquipTable row {actualTargetEquipId} to be a weapon row, got Type {targetEquip.Type}.");
                AssertEqual(0, targetEquip.Site, $"Current weapon-banner draw {drawId} target equip Site");
                AssertEqual(true, AscNet.Common.Database.Character.IsOwnableEquipTemplate(targetEquip), $"Current weapon-banner draw {drawId} target equip ownable template");
            }
        }

        private static void AssertCurrentVersionJumpDrawTargetRows(IReadOnlyList<(int DrawId, int CharacterId)> expectedTargets, IReadOnlyList<DrawInfo> drawInfos)
        {
            Dictionary<int, DrawInfo> drawInfoById = drawInfos.ToDictionary(info => info.Id);
            AssertEqual(expectedTargets.Count, drawInfoById.Count, "Current version-jump draw target row draw count");

            foreach ((int drawId, int expectedCharacterId) in expectedTargets)
            {
                if (!drawInfoById.TryGetValue(drawId, out DrawInfo? drawInfo))
                    throw new InvalidDataException($"Current version-jump draw target {drawId}: expected DrawInfo row.");
                if (!drawInfo.ResourceIds.TryGetValue(1, out int actualCharacterId))
                    throw new InvalidDataException($"Current version-jump draw target {drawId}: expected ResourceIds[1] target character id.");

                AssertEqual(expectedCharacterId, actualCharacterId, $"Current version-jump draw target {drawId} ResourceIds[1]");
            }

            int[] expectedCharacterIds = expectedTargets
                .Select(target => target.CharacterId)
                .Distinct()
                .OrderBy(characterId => characterId)
                .ToArray();
            int[] actualCharacterIds = drawInfos
                .Select(drawInfo => drawInfo.ResourceIds[1])
                .Distinct()
                .OrderBy(characterId => characterId)
                .ToArray();
            AssertIntegerList(
                expectedCharacterIds.Select(characterId => (long)characterId).ToArray(),
                actualCharacterIds.Select(characterId => (long)characterId).ToArray(),
                "Current version-jump distinct target character ids");
            AssertCurrentDrawTargetCharacterPersistenceRows(expectedCharacterIds);
        }

        private static void AssertCurrentCharacterGradeTablesAndPromotionBehavior()
        {
            List<CharacterTable> characterRows = TableReaderV2.Parse<CharacterTable>();
            List<CharacterGradeTable> gradeRows = TableReaderV2.Parse<CharacterGradeTable>();
            Dictionary<int, List<CharacterGradeTable>> gradeRowsByCharacterId = gradeRows
                .GroupBy(grade => grade.CharacterId)
                .ToDictionary(group => group.Key, group => group.OrderBy(grade => grade.Grade).ToList());

            foreach (CharacterTable characterRow in characterRows)
            {
                if (!gradeRowsByCharacterId.TryGetValue(characterRow.Id, out List<CharacterGradeTable>? characterGradeRows))
                    throw new InvalidDataException($"CharacterGradeTable: missing grade rows for CharacterTable row {characterRow.Id}.");

                AssertEqual(14, characterGradeRows.Count, $"CharacterGradeTable character {characterRow.Id} complete grade row count");
                AssertIntegerList(
                    Enumerable.Range(1, 14).Select(grade => (long)grade).ToArray(),
                    characterGradeRows.Select(grade => (long)grade.Grade).ToArray(),
                    $"CharacterGradeTable character {characterRow.Id} sequential grades");
            }

            const int lunaOblivionId = 1171004;
            if (!gradeRowsByCharacterId.TryGetValue(lunaOblivionId, out List<CharacterGradeTable>? lunaGradeRows))
                throw new InvalidDataException("Luna Oblivion CharacterGradeTable: expected grade rows.");

            CharacterGradeTable lunaGradeOne = lunaGradeRows.Single(grade => grade.Grade == 1);
            AssertEqual(AscNet.Common.Database.Inventory.Coin, lunaGradeOne.UseItemKey ?? -1, "Luna Oblivion CharacterGradeTable grade 1 cog cost item");
            AssertEqual(5000, lunaGradeOne.UseItemCount ?? -1, "Luna Oblivion CharacterGradeTable grade 1 cog cost count");
            foreach (CharacterGradeTable gradeRow in lunaGradeRows)
            {
                if (gradeRow.AttrId <= 0)
                    throw new InvalidDataException($"Luna Oblivion CharacterGradeTable grade {gradeRow.Grade}: expected nonzero AttrId, got {gradeRow.AttrId}.");
            }

            AssertLunaOblivionGradePromotionHandlerBehavior();
            AssertCharacterSetCollectStateHandlerBehavior();
        }

        private static void AssertLunaOblivionGradePromotionHandlerBehavior()
        {
            const int lunaOblivionId = 1171004;
            const long playerId = 880008;
            const int promoteGradePacketId = 8806;
            const int initialCogCount = 7000;
            const int expectedCogCount = initialCogCount - 5000;

            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            AscNet.Common.Database.AddCharacterRet addedCharacter = character.AddCharacter(lunaOblivionId);
            AssertEqual(1, addedCharacter.Character.Grade, "CharacterPromoteGradeRequestHandler Luna Oblivion initial AddCharacter grade");
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(
                playerId,
                [new Item { Id = AscNet.Common.Database.Inventory.Coin, Count = initialCogCount }]);

            CharacterPromoteGradeRequest request = new()
            {
                TemplateId = lunaOblivionId
            };
            CharacterPromoteGradeRequest requestRoundTrip = MessagePackSerializer.Deserialize<CharacterPromoteGradeRequest>(
                MessagePackSerializer.Serialize(request));
            AssertEqual(lunaOblivionId, requestRoundTrip.TemplateId, "CharacterPromoteGradeRequest Luna Oblivion TemplateId MessagePack round-trip");

            using (LoopbackSessionHarness harness = new(
                character,
                CreateDrawCompatibilityPlayer(playerId),
                inventory,
                "luna-grade-promotion-compat-test"))
            {
                InvokeRegisteredRequestHandler("CharacterPromoteGradeRequest", harness.Session, promoteGradePacketId, requestRoundTrip);

                Packet itemPushPacket = harness.ReadPacket("CharacterPromoteGradeRequestHandler NotifyItemDataList push");
                AssertEqual(Packet.ContentType.Push, itemPushPacket.Type, "CharacterPromoteGradeRequestHandler first packet type");
                Packet.Push itemPush = MessagePackSerializer.Deserialize<Packet.Push>(itemPushPacket.Content);
                AssertEqual(nameof(NotifyItemDataList), itemPush.Name, "CharacterPromoteGradeRequestHandler first push");
                NotifyItemDataList notifyItemDataList = MessagePackSerializer.Deserialize<NotifyItemDataList>(itemPush.Content);
                AssertEqual(1, notifyItemDataList.ItemDataList.Count, "CharacterPromoteGradeRequestHandler notified item count");
                Item notifiedCogs = notifyItemDataList.ItemDataList.Single(item => item.Id == AscNet.Common.Database.Inventory.Coin);
                AssertEqual((long)expectedCogCount, notifiedCogs.Count, "CharacterPromoteGradeRequestHandler NotifyItemDataList cog count after promotion");

                Packet characterPushPacket = harness.ReadPacket("CharacterPromoteGradeRequestHandler NotifyCharacterDataList push");
                AssertEqual(Packet.ContentType.Push, characterPushPacket.Type, "CharacterPromoteGradeRequestHandler second packet type");
                Packet.Push characterPush = MessagePackSerializer.Deserialize<Packet.Push>(characterPushPacket.Content);
                AssertEqual(nameof(NotifyCharacterDataList), characterPush.Name, "CharacterPromoteGradeRequestHandler second push");
                NotifyCharacterDataList notifyCharacterDataList = MessagePackSerializer.Deserialize<NotifyCharacterDataList>(characterPush.Content);
                AssertEqual(1, notifyCharacterDataList.CharacterDataList.Count, "CharacterPromoteGradeRequestHandler notified character count");
                CharacterData notifiedLuna = notifyCharacterDataList.CharacterDataList.Single(characterData => characterData.Id == lunaOblivionId);
                AssertEqual(2, notifiedLuna.Grade, "CharacterPromoteGradeRequestHandler NotifyCharacterDataList Luna Oblivion grade after promotion");

                CharacterPromoteGradeResponse response = ReadResponsePayload<CharacterPromoteGradeResponse>(
                    harness,
                    promoteGradePacketId,
                    nameof(CharacterPromoteGradeResponse),
                    "CharacterPromoteGradeRequestHandler response");
                AssertEqual(0, response.Code, "CharacterPromoteGradeResponse Code");
            }

            AssertEqual(2, addedCharacter.Character.Grade, "CharacterPromoteGradeRequestHandler Luna Oblivion session grade after promotion");
            Item sessionCogs = inventory.Items.Single(item => item.Id == AscNet.Common.Database.Inventory.Coin);
            AssertEqual((long)expectedCogCount, sessionCogs.Count, "CharacterPromoteGradeRequestHandler session inventory cog count after promotion");
        }

        private static void AssertCharacterSetCollectStateHandlerBehavior()
        {
            const int characterId = 1171004;
            const int missingCharacterId = 1021001;
            const long playerId = 880009;
            const int templateIdCollectStatePacketId = 8807;
            const int characterIdCollectStatePacketId = 8808;
            const int idCollectStatePacketId = 8809;
            const int missingCollectStatePacketId = 8810;

            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            AscNet.Common.Database.AddCharacterRet addedCharacter = character.AddCharacter(characterId);
            addedCharacter.Character.CollectState = false;

            CharacterSetCollectStateRequest RoundTripCollectStateRequest(
                CharacterSetCollectStateRequest source,
                string explicitIdFieldName,
                int expectedTargetCharacterId,
                bool expectedCollectState,
                string name)
            {
                byte[] serialized = MessagePackSerializer.Serialize(source);
                AssertCharacterSetCollectStateRequestSkipsTargetCharacterId(serialized, name);
                CharacterSetCollectStateRequest roundTrip = MessagePackSerializer.Deserialize<CharacterSetCollectStateRequest>(serialized);

                if (explicitIdFieldName == nameof(CharacterSetCollectStateRequest.TemplateId))
                    AssertEqual(expectedTargetCharacterId, roundTrip.TemplateId, $"{name} TemplateId MessagePack round-trip");
                else if (explicitIdFieldName == nameof(CharacterSetCollectStateRequest.CharacterId))
                    AssertEqual(expectedTargetCharacterId, roundTrip.CharacterId, $"{name} CharacterId MessagePack round-trip");
                else if (explicitIdFieldName == nameof(CharacterSetCollectStateRequest.Id))
                    AssertEqual(expectedTargetCharacterId, roundTrip.Id, $"{name} Id MessagePack round-trip");
                else
                    throw new InvalidDataException($"{name}: unknown explicit id field '{explicitIdFieldName}'.");

                AssertEqual(expectedCollectState, roundTrip.CollectState, $"{name} CollectState MessagePack round-trip");
                AssertEqual(expectedTargetCharacterId, roundTrip.TargetCharacterId, $"{name} resolved TargetCharacterId after MessagePack round-trip");
                return roundTrip;
            }

            static void AssertCharacterSetCollectStateRequestSkipsTargetCharacterId(byte[] serialized, string name)
            {
                System.Buffers.ReadOnlySequence<byte> sequence = new(serialized);
                MessagePackReader reader = new(sequence);
                int fieldCount = reader.ReadMapHeader();
                bool sawTargetCharacterId = false;

                for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                {
                    string fieldName = reader.ReadString()
                        ?? throw new InvalidDataException($"{name}: MessagePack field name serialized as nil.");
                    if (fieldName == nameof(CharacterSetCollectStateRequest.TargetCharacterId))
                        sawTargetCharacterId = true;
                    reader.Skip();
                }

                AssertEqual(false, sawTargetCharacterId, $"{name} TargetCharacterId omitted from MessagePack payload");
            }

            void AssertOwnedCollectStateRequest(
                CharacterSetCollectStateRequest request,
                string explicitIdFieldName,
                int packetId,
                bool expectedCollectState,
                string aliasName,
                string harnessName)
            {
                CharacterSetCollectStateRequest requestRoundTrip = RoundTripCollectStateRequest(
                    request,
                    explicitIdFieldName,
                    characterId,
                    expectedCollectState,
                    $"CharacterSetCollectStateRequest {aliasName}");

                using (LoopbackSessionHarness harness = new(
                    character,
                    CreateDrawCompatibilityPlayer(playerId),
                    CreateDrawCompatibilityInventory(playerId, []),
                    harnessName))
                {
                    InvokeRegisteredRequestHandler("CharacterSetCollectStateRequest", harness.Session, packetId, requestRoundTrip);

                    Packet characterPushPacket = harness.ReadPacket($"CharacterSetCollectStateRequestHandler {aliasName} NotifyCharacterDataList push");
                    AssertEqual(Packet.ContentType.Push, characterPushPacket.Type, $"CharacterSetCollectStateRequestHandler {aliasName} first packet type");
                    Packet.Push characterPush = MessagePackSerializer.Deserialize<Packet.Push>(characterPushPacket.Content);
                    AssertEqual(nameof(NotifyCharacterDataList), characterPush.Name, $"CharacterSetCollectStateRequestHandler {aliasName} first push");
                    NotifyCharacterDataList notifyCharacterDataList = MessagePackSerializer.Deserialize<NotifyCharacterDataList>(characterPush.Content);
                    AssertEqual(1, notifyCharacterDataList.CharacterDataList.Count, $"CharacterSetCollectStateRequestHandler {aliasName} notified character count");
                    CharacterData notifiedCharacter = notifyCharacterDataList.CharacterDataList.Single(characterData => characterData.Id == characterId);
                    AssertEqual(expectedCollectState, notifiedCharacter.CollectState, $"CharacterSetCollectStateRequestHandler {aliasName} NotifyCharacterDataList CollectState after toggle");

                    CharacterSetCollectStateResponse response = ReadResponsePayload<CharacterSetCollectStateResponse>(
                        harness,
                        packetId,
                        nameof(CharacterSetCollectStateResponse),
                        $"CharacterSetCollectStateRequestHandler {aliasName} response");
                    AssertEqual(0, response.Code, $"CharacterSetCollectStateResponse {aliasName} Code");
                }

                AssertEqual(expectedCollectState, addedCharacter.Character.CollectState, $"CharacterSetCollectStateRequestHandler session CollectState after {aliasName} toggle");
            }

            AssertOwnedCollectStateRequest(
                new CharacterSetCollectStateRequest
                {
                    TemplateId = characterId,
                    CollectState = true
                },
                nameof(CharacterSetCollectStateRequest.TemplateId),
                templateIdCollectStatePacketId,
                expectedCollectState: true,
                "TemplateId alias",
                "character-collect-state-template-id-compat-test");

            AssertOwnedCollectStateRequest(
                new CharacterSetCollectStateRequest
                {
                    CharacterId = characterId,
                    CollectState = false
                },
                nameof(CharacterSetCollectStateRequest.CharacterId),
                characterIdCollectStatePacketId,
                expectedCollectState: false,
                "CharacterId alias",
                "character-collect-state-character-id-compat-test");

            AssertOwnedCollectStateRequest(
                new CharacterSetCollectStateRequest
                {
                    Id = characterId,
                    CollectState = true
                },
                nameof(CharacterSetCollectStateRequest.Id),
                idCollectStatePacketId,
                expectedCollectState: true,
                "Id alias",
                "character-collect-state-id-compat-test");

            CharacterSetCollectStateRequest missingRequest = new()
            {
                TemplateId = missingCharacterId,
                CollectState = false
            };
            CharacterSetCollectStateRequest missingRequestRoundTrip = RoundTripCollectStateRequest(
                missingRequest,
                nameof(CharacterSetCollectStateRequest.TemplateId),
                missingCharacterId,
                expectedCollectState: false,
                "CharacterSetCollectStateRequest missing TemplateId");

            using (LoopbackSessionHarness harness = new(
                character,
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "character-collect-state-missing-compat-test"))
            {
                InvokeRegisteredRequestHandler("CharacterSetCollectStateRequest", harness.Session, missingCollectStatePacketId, missingRequestRoundTrip);

                CharacterSetCollectStateResponse response = ReadResponsePayload<CharacterSetCollectStateResponse>(
                    harness,
                    missingCollectStatePacketId,
                    nameof(CharacterSetCollectStateResponse),
                    "CharacterSetCollectStateRequestHandler missing character response without NotifyCharacterDataList");
                AssertEqual(20009011, response.Code, "CharacterSetCollectStateResponse missing character Code");
            }

            AssertEqual(true, addedCharacter.Character.CollectState, "CharacterSetCollectStateRequestHandler missing character leaves owned CollectState unchanged");
            AssertEqual(1, character.Characters.Count, "CharacterSetCollectStateRequestHandler missing character does not add character data");
        }

        private static void AssertCurrentDrawTargetCharacterPersistenceRows(IReadOnlyList<int> characterIds)
        {
            Dictionary<int, CharacterTable> characterRowsById = TableReaderV2.Parse<CharacterTable>().ToDictionary(character => character.Id);
            Dictionary<int, FashionTable> fashionRowsById = TableReaderV2.Parse<FashionTable>().ToDictionary(fashion => fashion.Id);
            Dictionary<int, EquipTable> equipRowsById = TableReaderV2.Parse<EquipTable>().ToDictionary(equip => equip.Id);
            Dictionary<int, CharacterSkillTable> skillRowsByCharacterId = TableReaderV2.Parse<CharacterSkillTable>().ToDictionary(skill => skill.CharacterId);
            ILookup<int, CharacterQualityTable> qualityRowsByCharacterId = TableReaderV2.Parse<CharacterQualityTable>().ToLookup(quality => quality.CharacterId);
            bool sawTargetWithMoreThanEightInitialSkills = false;
            bool sawTargetWithOwnableDefaultEquipRepair = false;
            bool sawLunaOblivionChakramDefault = false;

            foreach (int characterId in characterIds.Distinct())
            {
                if (!characterRowsById.TryGetValue(characterId, out CharacterTable? characterRow))
                    throw new InvalidDataException($"Current draw target character {characterId}: expected CharacterTable row.");

                if (characterId == 1171004)
                {
                    sawLunaOblivionChakramDefault = true;
                    AssertEqual(43, characterRow.EquipType, "Luna Oblivion CharacterTable EquipType uses Chakram");
                    AssertEqual(2434001, characterRow.EquipId, "Luna Oblivion CharacterTable EquipId uses 4-star Chakram Reappearance");
                    if (!equipRowsById.TryGetValue(characterRow.EquipId, out EquipTable? lunaDefaultEquipRow))
                        throw new InvalidDataException("Luna Oblivion: expected local EquipTable row 2434001 for Chakram Reappearance.");
                    AssertEqual(43, lunaDefaultEquipRow.Type, "Luna Oblivion default equip Type");
                    AssertEqual("Reappearance", lunaDefaultEquipRow.Name, "Luna Oblivion default equip Name");
                }

                int defaultFashionId = characterRow.DefaultNpcFashtionId;
                if (defaultFashionId != 0)
                {
                    if (!fashionRowsById.TryGetValue(defaultFashionId, out FashionTable? defaultFashionRow))
                        throw new InvalidDataException($"Current draw target character {characterId}: expected FashionTable row {defaultFashionId} for CharacterTable DefaultNpcFashtionId.");
                    AssertEqual(characterId, defaultFashionRow.CharacterId, $"Current draw target character {characterId} default fashion CharacterId");
                }

                if (!skillRowsByCharacterId.TryGetValue(characterId, out CharacterSkillTable? skillRow))
                    throw new InvalidDataException($"Current draw target character {characterId}: expected CharacterSkillTable row.");

                CharacterQualityTable firstQualityRow = qualityRowsByCharacterId[characterId]
                    .OrderBy(quality => quality.Quality)
                    .FirstOrDefault()
                    ?? throw new InvalidDataException($"Current draw target character {characterId}: expected CharacterQualityTable row.");
                if (firstQualityRow.Quality <= 0)
                    throw new InvalidDataException($"Current draw target character {characterId}: expected nonzero first quality, got {firstQualityRow.Quality}.");

                uint[] expectedSkillIds = skillRow.SkillGroupId
                    .Where(skillGroupId => skillGroupId > 0)
                    .Select(skillGroupId => AddCharacterSkillIdFromGroup(characterId, skillGroupId))
                    .Distinct()
                    .ToArray();
                if (expectedSkillIds.Length > 8)
                    sawTargetWithMoreThanEightInitialSkills = true;

                AscNet.Common.Database.Character roster = CreateDrawCompatibilityCharacter(characterId);
                AscNet.Common.Database.AddCharacterRet addedCharacter = roster.AddCharacter((uint)characterId);
                AssertEqual((uint)characterId, addedCharacter.Character.Id, $"Current draw target character {characterId} AddCharacter persisted id");
                AssertEqual(firstQualityRow.Quality, addedCharacter.Character.Quality, $"Current draw target character {characterId} AddCharacter first quality");
                AssertEqual(firstQualityRow.Quality, addedCharacter.Character.InitQuality, $"Current draw target character {characterId} AddCharacter init quality");
                AssertIntegerList(
                    expectedSkillIds.Select(skillId => (long)skillId).ToArray(),
                    addedCharacter.Character.SkillList.Select(skill => (long)skill.Id).ToArray(),
                    $"Current draw target character {characterId} AddCharacter all positive skill ids");

                AscNet.Common.Database.Character persistedCharacter = new()
                {
                    Uid = characterId,
                    Characters =
                    [
                        new CharacterData
                        {
                            Id = (uint)characterId,
                            SkillList = null!,
                            EnhanceSkillList = null!,
                            FashionId = 0,
                            CharacterHeadInfo = null!
                        }
                    ],
                    Equips = [],
                    Fashions = []
                };

                if (characterId == 1171004)
                {
                    persistedCharacter.Equips.Add(new EquipData
                    {
                        Id = 990001,
                        TemplateId = 2484001,
                        CharacterId = characterId,
                        Level = 1,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = [],
                        WeaponOverrunData = new()
                    });
                }

                AssertEqual(true, persistedCharacter.NormalizeCharactersForCurrentTables(), $"Current draw target character {characterId} persisted document repair reports mutation");
                CharacterData repairedCharacter = persistedCharacter.Characters.Single(character => character.Id == characterId);
                AssertEqual(1, repairedCharacter.Level, $"Current draw target character {characterId} repaired Level");
                AssertEqual(firstQualityRow.Quality, repairedCharacter.Quality, $"Current draw target character {characterId} repaired Quality");
                AssertEqual(firstQualityRow.Quality, repairedCharacter.InitQuality, $"Current draw target character {characterId} repaired InitQuality");
                AssertEqual(1, repairedCharacter.Grade, $"Current draw target character {characterId} repaired Grade");
                AssertEqual(1, repairedCharacter.TrustLv, $"Current draw target character {characterId} repaired TrustLv");
                AssertEqual(1, repairedCharacter.LiberateLv, $"Current draw target character {characterId} repaired LiberateLv");
                if (repairedCharacter.CreateTime <= 0)
                    throw new InvalidDataException($"Current draw target character {characterId}: expected repaired CreateTime to be nonzero.");
                if (repairedCharacter.EnhanceSkillList is null)
                    throw new InvalidDataException($"Current draw target character {characterId}: expected repaired EnhanceSkillList.");
                AssertIntegerList(
                    expectedSkillIds.Order().Select(skillId => (long)skillId).ToArray(),
                    repairedCharacter.SkillList.Select(skill => (long)skill.Id).ToArray(),
                    $"Current draw target character {characterId} repaired all positive skill ids");

                if (defaultFashionId > 0)
                {
                    AssertEqual((uint)defaultFashionId, repairedCharacter.FashionId, $"Current draw target character {characterId} repaired FashionId");
                    if (repairedCharacter.CharacterHeadInfo is null)
                        throw new InvalidDataException($"Current draw target character {characterId}: expected repaired CharacterHeadInfo.");
                    AssertEqual((uint)defaultFashionId, repairedCharacter.CharacterHeadInfo.HeadFashionId, $"Current draw target character {characterId} repaired HeadFashionId");
                    FashionList repairedFashion = persistedCharacter.Fashions.Single(fashion => fashion.Id == defaultFashionId);
                    AssertEqual(false, repairedFashion.IsLock, $"Current draw target character {characterId} repaired default fashion unlocked");
                }

                if (characterRow.EquipId > 0
                    && equipRowsById.TryGetValue(characterRow.EquipId, out EquipTable? defaultEquipRow)
                    && AscNet.Common.Database.Character.IsOwnableEquipTemplate(defaultEquipRow))
                {
                    sawTargetWithOwnableDefaultEquipRepair = true;
                    EquipData repairedDefaultEquip = persistedCharacter.Equips.Single(equip => equip.TemplateId == (uint)characterRow.EquipId);
                    AssertEqual(characterId, repairedDefaultEquip.CharacterId, $"Current draw target character {characterId} repaired default equip CharacterId");
                    AssertEqual(1, repairedDefaultEquip.Level, $"Current draw target character {characterId} repaired default equip Level");
                    if (characterId == 1171004)
                    {
                        EquipData previousWrongDefaultEquip = persistedCharacter.Equips.Single(equip => equip.TemplateId == 2484001);
                        AssertEqual(0, previousWrongDefaultEquip.CharacterId, "Luna Oblivion previous Rasetsu default is unassigned during repair");
                    }
                }
            }

            if (!sawTargetWithMoreThanEightInitialSkills)
                throw new InvalidDataException("Current draw target characters: expected at least one row with more than eight positive CharacterSkill.SkillGroupId-derived initial skills.");
            if (!sawTargetWithOwnableDefaultEquipRepair)
                throw new InvalidDataException("Current draw target characters: expected at least one row with an ownable CharacterTable EquipId for default equip repair.");
            if (!sawLunaOblivionChakramDefault)
                throw new InvalidDataException("Current draw target characters: expected Luna Oblivion to be covered by default Chakram Reappearance assertions.");
        }

        private static uint AddCharacterSkillIdFromGroup(int characterId, int skillGroupId)
        {
            if (skillGroupId <= 0)
                throw new InvalidDataException($"Current draw target character {characterId}: expected positive AddCharacter skill group id, got {skillGroupId}.");

            string skillGroupIdText = skillGroupId.ToString();
            uint skillId = uint.Parse(skillGroupIdText[..Math.Min(6, skillGroupIdText.Length)]);
            if (skillId == 0)
                throw new InvalidDataException($"Current draw target character {characterId}: expected nonzero AddCharacter active skill id from group {skillGroupId}.");
            return skillId;
        }

        private static void AssertTargetCharacterPityDraw(long playerId, DrawInfo drawInfo, int? expectedTargetCharacterId = null, string? targetName = null)
        {
            string name = targetName is null
                ? $"Draw {drawInfo.Id} target character pity"
                : $"Draw {drawInfo.Id} {targetName} target character pity";
            if (!drawInfo.ResourceIds.TryGetValue(1, out int targetCharacterId))
                throw new InvalidDataException($"{name}: expected ResourceIds[1] target character id.");
            if (expectedTargetCharacterId.HasValue)
                AssertEqual(expectedTargetCharacterId.Value, targetCharacterId, $"{name} configured target character id");

            int pityPullOffset = drawInfo.MaxBottomTimes - (drawInfo.TotalCount % drawInfo.MaxBottomTimes) - 1;
            if (pityPullOffset < 0)
                pityPullOffset += drawInfo.MaxBottomTimes;

            List<RewardGoods> rewards = InvokeDrawDraw(playerId, drawInfo.Id, pityPullOffset, name);
            AssertEqual(1, rewards.Count, $"{name} reward count");
            RewardGoods reward = rewards[0];
            AssertEqual((int)RewardType.Character, reward.RewardType, $"{name} reward type");
            AssertEqual(targetCharacterId, reward.TemplateId, $"{name} target character id");
            if (expectedTargetCharacterId.HasValue)
                AssertEqual(expectedTargetCharacterId.Value, reward.TemplateId, $"{name} expected target character reward id");
            AssertEqual(1, reward.Count, $"{name} reward count value");
            AssertEqual(1, reward.Level, $"{name} reward level");
        }

        private static void AssertMethodContainsIntConstants(MethodInfo method, IReadOnlyList<IlInstruction> instructions, IReadOnlyList<int> expectedConstants, string name)
        {
            foreach (int expectedConstant in expectedConstants)
            {
                if (!instructions.Any(instruction => LdcI4Value(instruction) == expectedConstant))
                    throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} IL to contain constant {expectedConstant}.");
            }
        }

        private static void AssertTargetWeaponPityDraw(long playerId, DrawInfo drawInfo)
        {
            if (!drawInfo.ResourceIds.TryGetValue(1, out int targetEquipId))
                throw new InvalidDataException($"Draw {drawInfo.Id} target weapon pity: expected ResourceIds[1] target equip id.");
            int pityPullOffset = drawInfo.MaxBottomTimes - (drawInfo.TotalCount % drawInfo.MaxBottomTimes) - 1;
            if (pityPullOffset < 0)
                pityPullOffset += drawInfo.MaxBottomTimes;

            List<RewardGoods> rewards = InvokeDrawDraw(playerId, drawInfo.Id, pityPullOffset, $"Draw {drawInfo.Id} target weapon pity");
            AssertEqual(1, rewards.Count, $"Draw {drawInfo.Id} target weapon pity reward count");
            RewardGoods reward = rewards[0];
            AssertEqual((int)RewardType.Equip, reward.RewardType, $"Draw {drawInfo.Id} target weapon pity reward type");
            AssertEqual(targetEquipId, reward.TemplateId, $"Draw {drawInfo.Id} target weapon pity target equip id");
            AssertEqual(1, reward.Count, $"Draw {drawInfo.Id} target weapon pity reward count value");
            AssertEqual(1, reward.Level, $"Draw {drawInfo.Id} target weapon pity reward level");
        }

        private static List<RewardGoods> InvokeDrawDraw(long playerId, int drawId, int pullOffset, string name)
        {
            Type drawManagerType = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
            MethodInfo drawDraw = RequiredMethod(
                drawManagerType,
                "DrawDraw",
                BindingFlags.Static | BindingFlags.Public,
                [typeof(long), typeof(int), typeof(int)]);
            return drawDraw.Invoke(null, [playerId, drawId, pullOffset]) as List<RewardGoods>
                ?? throw new InvalidDataException($"{name}: DrawManager.DrawDraw returned nil or an unexpected payload.");
        }

        private static void AssertDrawRewardPushMatchesRewardGoods(DrawInfo drawInfo, RewardGoods reward, NotifyItemDataList drawItemPush, NotifyEquipDataList? drawEquipPush, string name)
        {
            if (reward.Count <= 0)
                throw new InvalidDataException($"{name}: expected positive reward count for template {reward.TemplateId}, got {reward.Count}.");

            switch ((RewardType)reward.RewardType)
            {
                case RewardType.Item:
                    long initialCount = reward.TemplateId == drawInfo.UseItemId ? drawInfo.UseItemCount * 4L : 0;
                    long spentCount = reward.TemplateId == drawInfo.UseItemId ? drawInfo.UseItemCount : 0;
                    Item itemReward = drawItemPush.ItemDataList.Single(item => item.Id == reward.TemplateId);
                    AssertEqual(initialCount - spentCount + reward.Count, itemReward.Count, $"{name} item reward count");
                    break;
                case RewardType.Equip:
                    if (drawEquipPush is null)
                        throw new InvalidDataException($"{name}: expected NotifyEquipDataList for equip reward {reward.TemplateId}.");
                    EquipData equipReward = drawEquipPush.EquipDataList.Single(equip => equip.TemplateId == (uint)reward.TemplateId);
                    AssertEqual(1, drawEquipPush.EquipDataList.Count, $"{name} equip reward push count");
                    AssertEqual((uint)reward.TemplateId, equipReward.TemplateId, $"{name} equip reward template");
                    break;
                default:
                    throw new InvalidDataException($"{name}: unexpected reward type {(RewardType)reward.RewardType} for draw {drawInfo.Id}.");
            }
        }

        private static void ValidateCommandCompatibility()
        {
            AscNet.GameServer.Commands.CommandFactory.commands.Clear();
            AscNet.GameServer.Commands.CommandFactory.LoadCommands();

            Type blackCardCommandType = AscNet.GameServer.Commands.CommandFactory.commands.GetValueOrDefault("bc")
                ?? throw new InvalidDataException("CommandFactory.LoadCommands: expected command 'bc' to be discoverable.");
            AssertEqual("AscNet.GameServer.Commands.BlackCardCommand", blackCardCommandType.FullName, "CommandFactory.LoadCommands command 'bc' type");

            AssertBlackCardCommandAccepts(blackCardCommandType, [], "BlackCardCommand metadata no-arg grant");
            AssertBlackCardCommandAccepts(blackCardCommandType, ["45000"], "BlackCardCommand metadata numeric grant");
            AssertBlackCardCommandAccepts(blackCardCommandType, ["max"], "BlackCardCommand metadata max grant");
            AssertBlackCardCommandRejects(["0"], "BlackCardCommand metadata rejects zero grant");
            AssertBlackCardCommandRejects(["abc"], "BlackCardCommand metadata rejects non-numeric grant");

            AssertBlackCardCommandPersistenceContract(blackCardCommandType);

            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDrawCompatibility();
            ExecuteBlackCardCommandAndAssertGrant([], initialBlackCards: 0, expectedBlackCards: 30_000, playerId: 90_001, "BlackCardCommand default grant behavior");
            ExecuteBlackCardCommandAndAssertGrant(["875"], initialBlackCards: 125, expectedBlackCards: 1_000, playerId: 90_002, "BlackCardCommand explicit grant behavior");
        }

        private static void AssertBlackCardCommandAccepts(Type expectedType, string[] args, string name)
        {
            AscNet.GameServer.Commands.Command command;
            try
            {
                command = AscNet.GameServer.Commands.CommandFactory.CreateCommand("bc", null!, args)
                    ?? throw new InvalidDataException($"{name}: expected CommandFactory.CreateCommand to create command 'bc'.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException)
            {
                throw new InvalidDataException($"{name}: expected args [{string.Join(", ", args)}] to pass command validation.", exception.InnerException);
            }

            AssertEqual(expectedType, command.GetType(), $"{name} resolved command type");
        }

        private static void AssertBlackCardCommandRejects(string[] args, string name)
        {
            try
            {
                _ = AscNet.GameServer.Commands.CommandFactory.CreateCommand("bc", null!, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException)
            {
                return;
            }
            catch (ArgumentException)
            {
                return;
            }

            throw new InvalidDataException($"{name}: expected args [{string.Join(", ", args)}] to fail command validation.");
        }

        private static void AssertBlackCardCommandPersistenceContract(Type blackCardCommandType)
        {
            MethodInfo execute = RequiredMethod(
                blackCardCommandType,
                "Execute",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo inventoryDo = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                nameof(AscNet.Common.Database.Inventory.Do),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int), typeof(int)]);
            MethodInfo inventorySave = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                nameof(AscNet.Common.Database.Inventory.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo sendPush = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendPush),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 1);

            AssertMethodTransitivelyCalls(execute, inventorySave, "BlackCardCommand.Execute persistence");
            AssertMethodTransitivelyCallsGenericMethod(execute, sendPush, typeof(NotifyItemDataList), "BlackCardCommand.Execute item notification");

            List<IlInstruction> instructions = ReadIlInstructions(execute).ToList();
            int grantIndex = FindMethodCallWithRecentConstants(instructions, inventoryDo, AscNet.Common.Database.Inventory.FreeGem);
            if (grantIndex < 0)
                throw new InvalidDataException("BlackCardCommand.Execute persistence: expected Inventory.Do(Inventory.FreeGem, amount) to grant Black Cards.");

            int saveIndex = FindCallIndex(instructions, inventorySave, grantIndex + 1);
            if (saveIndex < 0)
                throw new InvalidDataException("BlackCardCommand.Execute persistence: expected Inventory.Save after granting Black Cards.");

            int notifyIndex = FindGenericCallIndex(instructions, sendPush, typeof(NotifyItemDataList), grantIndex + 1);
            if (notifyIndex < 0)
                throw new InvalidDataException("BlackCardCommand.Execute item notification: expected Session.SendPush<NotifyItemDataList> after granting Black Cards.");
        }

        private static void ExecuteBlackCardCommandAndAssertGrant(string[] args, long initialBlackCards, long expectedBlackCards, long playerId, string name)
        {
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(
                playerId,
                [
                    new Item { Id = AscNet.Common.Database.Inventory.FreeGem, Count = initialBlackCards }
                ]);
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                inventory,
                $"{name.Replace(' ', '-').ToLowerInvariant()}-test");

            AscNet.GameServer.Commands.Command command = AscNet.GameServer.Commands.CommandFactory.CreateCommand("bc", harness.Session, args)
                ?? throw new InvalidDataException($"{name}: expected CommandFactory.CreateCommand to create command 'bc'.");
            command.Execute();

            Item inventoryBlackCards = inventory.Items.Single(item => item.Id == AscNet.Common.Database.Inventory.FreeGem);
            AssertEqual(expectedBlackCards, inventoryBlackCards.Count, $"{name} inventory Black Card count");

            Packet pushPacket = harness.ReadPacket($"{name} NotifyItemDataList push");
            AssertEqual(Packet.ContentType.Push, pushPacket.Type, $"{name} packet type");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(pushPacket.Content);
            AssertEqual(nameof(NotifyItemDataList), push.Name, $"{name} push name");
            NotifyItemDataList notifyItemDataList = MessagePackSerializer.Deserialize<NotifyItemDataList>(push.Content);
            AssertEqual(1, notifyItemDataList.ItemDataList.Count, $"{name} notified item count");
            Item notifiedBlackCards = notifyItemDataList.ItemDataList.Single(item => item.Id == AscNet.Common.Database.Inventory.FreeGem);
            AssertEqual(expectedBlackCards, notifiedBlackCards.Count, $"{name} notified Black Card count");
        }

        private static AscNet.Common.Database.Character CreateDrawCompatibilityCharacter(long uid)
        {
            return new AscNet.Common.Database.Character
            {
                Uid = uid,
                Characters = [],
                Equips = [],
                Fashions = []
            };
        }

        private static AscNet.Common.Database.Player CreateDrawCompatibilityPlayer(long playerId)
        {
            return new AscNet.Common.Database.Player
            {
                Token = $"draw-compat-{playerId}",
                PlayerData = new PlayerData
                {
                    Id = playerId,
                    Name = $"DrawCompat{playerId}",
                    Level = 80,
                    ServerId = "test"
                },
                HeadPortraits = [],
                TeamGroups = []
            };
        }

        private static AscNet.Common.Database.Inventory CreateDrawCompatibilityInventory(long uid, IEnumerable<Item> items)
        {
            return new AscNet.Common.Database.Inventory
            {
                Uid = uid,
                Items = items.ToList()
            };
        }

        private sealed class MongoCollectionOverride : IDisposable
        {
            private readonly (FieldInfo Field, object? Value)[] originalValues;

            private MongoCollectionOverride((FieldInfo Field, object? Replacement)[] replacements)
            {
                originalValues = replacements
                    .Select(replacement => (replacement.Field, replacement.Field.GetValue(null)))
                    .ToArray();

                foreach ((FieldInfo field, object? replacement) in replacements)
                    SetStaticReadonlyField(field, replacement);
            }

            public static MongoCollectionOverride InstallForDrawCompatibility()
            {
                return new MongoCollectionOverride(
                [
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Inventory)), CreateNoOpMongoCollection<AscNet.Common.Database.Inventory>()),
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Character)), CreateNoOpMongoCollection<AscNet.Common.Database.Character>())
                ]);
            }

            public void Dispose()
            {
                foreach ((FieldInfo field, object? value) in originalValues)
                    SetStaticReadonlyField(field, value);
            }

            private static FieldInfo RequiredCollectionField(Type databaseType)
            {
                return databaseType.GetField("collection", BindingFlags.Static | BindingFlags.Public)
                    ?? throw new MissingFieldException(databaseType.FullName, "collection");
            }

            private static IMongoCollection<TDocument> CreateNoOpMongoCollection<TDocument>()
            {
                return DispatchProxy.Create<IMongoCollection<TDocument>, NoOpMongoCollectionProxy<TDocument>>();
            }

            private static void SetStaticReadonlyField(FieldInfo field, object? value)
            {
                DynamicMethod setter = new(
                    $"Set_{field.DeclaringType?.Name}_{field.Name}",
                    typeof(void),
                    [typeof(object)],
                    typeof(Program),
                    skipVisibility: true);
                ILGenerator il = setter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                if (field.FieldType.IsValueType)
                    il.Emit(OpCodes.Unbox_Any, field.FieldType);
                else
                    il.Emit(OpCodes.Castclass, field.FieldType);
                il.Emit(OpCodes.Stsfld, field);
                il.Emit(OpCodes.Ret);

                ((Action<object?>)setter.CreateDelegate(typeof(Action<object?>))).Invoke(value);
            }
        }

        private class NoOpMongoCollectionProxy<TDocument> : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                Type returnType = targetMethod?.ReturnType ?? typeof(void);
                if (returnType == typeof(void))
                    return null;
                if (returnType == typeof(Task))
                    return Task.CompletedTask;
                if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    Type resultType = returnType.GetGenericArguments()[0];
                    object? result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                    return typeof(Task)
                        .GetMethod(nameof(Task.FromResult), BindingFlags.Public | BindingFlags.Static)!
                        .MakeGenericMethod(resultType)
                        .Invoke(null, [result]);
                }

                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
            }
        }

        private static void ValidateInventoryEquipCompatibility()
        {
            List<EquipTable> currentEquipRows = TableReaderV2.Parse<EquipTable>();
            EquipTable validEquipRow = currentEquipRows.FirstOrDefault(equip => equip.Id > 0 && AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip))
                ?? throw new InvalidDataException("EquipTable: expected at least one current-client ownable equip row.");
            EquipTable type99EquipRow = currentEquipRows.FirstOrDefault(equip => equip.Type == 99 && equip.Priority != 100)
                ?? throw new InvalidDataException("EquipTable: expected at least one current-client type 99 enhancer equip row.");
            EquipTable displayOnlyEquipRow = currentEquipRows.FirstOrDefault(equip => equip.Priority == 100)
                ?? throw new InvalidDataException("EquipTable: expected at least one priority-100 display-only equip row.");
            uint validTemplateId = (uint)validEquipRow.Id;
            uint type99TemplateId = (uint)type99EquipRow.Id;
            HashSet<uint> validTemplateIds = currentEquipRows.Select(equip => (uint)equip.Id).ToHashSet();
            uint invalidTemplateId = validTemplateIds.Contains(uint.MaxValue) ? uint.MaxValue - 1 : uint.MaxValue;
            if (invalidTemplateId == 0 || validTemplateIds.Contains(invalidTemplateId))
                throw new InvalidDataException("EquipTable: failed to choose a template id outside the current-client equip table.");

            AssertEqual(true, AscNet.Common.Database.Character.IsOwnableEquipTemplate(type99EquipRow), "Type 99 enhancer equip rows remain retail-owned equip data");
            AssertEqual(false, AscNet.Common.Database.Character.IsOwnableEquipTemplate(displayOnlyEquipRow), "Display-only equip rows are not owned equip data");
            ValidateEquipPutOnSlotSwapBehavior(currentEquipRows);

            AscNet.Common.Database.Character character = new()
            {
                Uid = 99,
                Characters = [],
                Equips =
                [
                    new EquipData
                    {
                        Id = 10,
                        TemplateId = invalidTemplateId,
                        CharacterId = 201,
                        Level = 1,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = []
                    },
                    new EquipData
                    {
                        Id = 11,
                        TemplateId = validTemplateId,
                        CharacterId = 202,
                        Level = 1,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = [],
                        IsRecycle = true
                    },
                    new EquipData
                    {
                        Id = 12,
                        TemplateId = 0,
                        CharacterId = 203,
                        Level = 1,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = []
                    },
                    new EquipData
                    {
                        Id = 13,
                        TemplateId = type99TemplateId,
                        CharacterId = 204,
                        Level = 1,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = []
                    },
                    new EquipData
                    {
                        Id = 14,
                        TemplateId = (uint)displayOnlyEquipRow.Id,
                        CharacterId = 205,
                        Level = 1,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = []
                    },
                    new EquipData
                    {
                        Id = 7,
                        TemplateId = validTemplateId,
                        CharacterId = 101,
                        Level = 0,
                        ResonanceInfo = null!,
                        UnconfirmedResonanceInfo = null!,
                        AwakeSlotList = null!
                    },
                    new EquipData
                    {
                        Id = 7,
                        TemplateId = validTemplateId,
                        CharacterId = 102,
                        Level = 2,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = []
                    },
                    new EquipData
                    {
                        Id = 0,
                        TemplateId = validTemplateId,
                        CharacterId = 103,
                        Level = 3,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = []
                    }
                ],
                Fashions = []
            };

            AssertEqual(true, character.NormalizeEquipsForCurrentTables(), "Character equip normalization reports mutation");
            AssertEqual(4, character.Equips.Count, "Character equip normalization retained current-client rows");
            if (character.Equips.Any(equip => !AscNet.Common.Database.Character.IsOwnableEquipTemplate(currentEquipRows.Single(row => row.Id == equip.TemplateId))))
                throw new InvalidDataException("Character equip normalization retained a display-only equip row.");
            if (character.Equips.Any(equip => equip.TemplateId == invalidTemplateId || equip.TemplateId == 0))
                throw new InvalidDataException("Character equip normalization retained an invalid template id.");
            if (character.Equips.Any(equip => equip.IsRecycle))
                throw new InvalidDataException("Character equip normalization retained a recycled equip.");

            int[] retainedCharacterIds = character.Equips.Select(equip => equip.CharacterId).Order().ToArray();
            int[] expectedCharacterIds = [101, 102, 103, 204];

            if (character.Equips.Any(equip => equip.Id == 0))
                throw new InvalidDataException("Character equip normalization left a zero equip instance id.");
            int uniqueInstanceIdCount = character.Equips.Select(equip => equip.Id).Distinct().Count();
            AssertEqual(character.Equips.Count, uniqueInstanceIdCount, "Character equip normalization unique instance ids");

            EquipData clampedEquip = character.Equips.Single(equip => equip.CharacterId == 101);
            AssertEqual(1, clampedEquip.Level, "Character equip normalization level clamp");
            foreach (EquipData equip in character.Equips)
            {
                if (equip.ResonanceInfo is null)
                    throw new InvalidDataException($"Character equip normalization left ResonanceInfo nil for instance {equip.Id}.");
                if (equip.UnconfirmedResonanceInfo is null)
                    throw new InvalidDataException($"Character equip normalization left UnconfirmedResonanceInfo nil for instance {equip.Id}.");
                if (equip.AwakeSlotList is null)
                    throw new InvalidDataException($"Character equip normalization left AwakeSlotList nil for instance {equip.Id}.");
                if (equip.WeaponOverrunData is null)
                    throw new InvalidDataException($"Character equip normalization left WeaponOverrunData nil for instance {equip.Id}.");
            }

            AssertEqual(false, character.NormalizeEquipsForCurrentTables(), "Character equip normalization idempotent second pass");

            AscNet.Common.Database.Character nullEquipListCharacter = new()
            {
                Uid = 100,
                Characters = [],
                Equips = null!,
                Fashions = []
            };
            AssertEqual(true, nullEquipListCharacter.NormalizeEquipsForCurrentTables(), "Character null equip list normalization reports mutation");
            AssertEmptyList(nullEquipListCharacter.Equips, "Character null equip list normalization result");
            AssertEqual(false, nullEquipListCharacter.NormalizeEquipsForCurrentTables(), "Character null equip list normalization idempotent second pass");

            NotifyEquipDataList notifyEquipDataList = new()
            {
                EquipDataList =
                [
                    new EquipData
                    {
                        Id = 2,
                        TemplateId = validTemplateId,
                        CharacterId = 301,
                        Level = 1,
                        Exp = 20,
                        Breakthrough = 1,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = [],
                        IsLock = true,
                        CreateTime = 1234
                    },
                    new EquipData
                    {
                        Id = 5,
                        TemplateId = validTemplateId,
                        CharacterId = 302,
                        Level = 3,
                        Exp = 40,
                        Breakthrough = 2,
                        ResonanceInfo = [],
                        UnconfirmedResonanceInfo = [],
                        AwakeSlotList = [],
                        CreateTime = 5678
                    }
                ]
            };
            NotifyEquipDataList roundTripEquipDataList = MessagePackSerializer.Deserialize<NotifyEquipDataList>(
                MessagePackSerializer.Serialize(notifyEquipDataList));
            AssertEqual(2, roundTripEquipDataList.EquipDataList.Count, "NotifyEquipDataList.EquipDataList MessagePack round-trip actual equip count");
            AssertEquipDataPayloadEquals(notifyEquipDataList.EquipDataList[0], roundTripEquipDataList.EquipDataList[0], "NotifyEquipDataList first actual equip round-trip");
            AssertEquipDataPayloadEquals(notifyEquipDataList.EquipDataList[1], roundTripEquipDataList.EquipDataList[1], "NotifyEquipDataList second actual equip round-trip");
            AssertIntegerList([], GetRequiredIntegerList(roundTripEquipDataList, "DeletedEquipIdList"), "NotifyEquipDataList.DeletedEquipIdList MessagePack default empty deletion list");
            SetRequiredIntegerList(notifyEquipDataList, "DeletedEquipIdList", [2, 5]);
            NotifyEquipDataList roundTripDeletedEquipDataList = MessagePackSerializer.Deserialize<NotifyEquipDataList>(
                MessagePackSerializer.Serialize(notifyEquipDataList));
            AssertIntegerList([2, 5], GetRequiredIntegerList(roundTripDeletedEquipDataList, "DeletedEquipIdList"), "NotifyEquipDataList.DeletedEquipIdList MessagePack explicit consumed equip ids");
            AssertNotifyEquipDataListHasNoPhantomCleanupPayload("NotifyEquipDataList phantom cleanup protocol");

            NotifyEquipChipAutoRecycleSite notifyEquipChipAutoRecycleSite = new()
            {
                ChipRecycleSite = new()
                {
                    RecycleStar = [1, 2, 3, 4],
                    Days = 7,
                    SetRecycleTime = 123456
                }
            };
            NotifyEquipChipAutoRecycleSite roundTripEquipChipAutoRecycleSite = MessagePackSerializer.Deserialize<NotifyEquipChipAutoRecycleSite>(
                MessagePackSerializer.Serialize(notifyEquipChipAutoRecycleSite));
            ChipRecycleSite roundTripChipRecycleSite = roundTripEquipChipAutoRecycleSite.ChipRecycleSite
                ?? throw new InvalidDataException("NotifyEquipChipAutoRecycleSite ChipRecycleSite MessagePack round-trip serialized as nil.");
            AssertEqual(4, roundTripChipRecycleSite.RecycleStar.Count, "NotifyEquipChipAutoRecycleSite ChipRecycleSite.RecycleStar MessagePack round-trip count");
            AssertEqual(1, roundTripChipRecycleSite.RecycleStar[0], "NotifyEquipChipAutoRecycleSite ChipRecycleSite.RecycleStar first star");
            AssertEqual(2, roundTripChipRecycleSite.RecycleStar[1], "NotifyEquipChipAutoRecycleSite ChipRecycleSite.RecycleStar second star");
            AssertEqual(3, roundTripChipRecycleSite.RecycleStar[2], "NotifyEquipChipAutoRecycleSite ChipRecycleSite.RecycleStar third star");
            AssertEqual(4, roundTripChipRecycleSite.RecycleStar[3], "NotifyEquipChipAutoRecycleSite ChipRecycleSite.RecycleStar fourth star");
            AssertEqual(7, roundTripChipRecycleSite.Days, "NotifyEquipChipAutoRecycleSite ChipRecycleSite.Days MessagePack round-trip");
            AssertEqual(123456, roundTripChipRecycleSite.SetRecycleTime, "NotifyEquipChipAutoRecycleSite ChipRecycleSite.SetRecycleTime MessagePack round-trip");

            NotifyEquipGuideData notifyEquipGuideData = new()
            {
                EquipGuideData = new()
                {
                    TargetId = 1001,
                    CharacterId = 1021001,
                    PutOnPosList = [1, 3, 5],
                    FinishedTargets = []
                }
            };
            NotifyEquipGuideData roundTripEquipGuideData = MessagePackSerializer.Deserialize<NotifyEquipGuideData>(
                MessagePackSerializer.Serialize(notifyEquipGuideData));
            NotifyEquipGuideData.NotifyEquipGuideDataEquipGuideData roundTripGuideData = roundTripEquipGuideData.EquipGuideData
                ?? throw new InvalidDataException("NotifyEquipGuideData EquipGuideData MessagePack round-trip serialized as nil.");
            AssertEqual(1001, roundTripGuideData.TargetId, "NotifyEquipGuideData EquipGuideData.TargetId MessagePack round-trip");
            AssertEqual(1021001, roundTripGuideData.CharacterId, "NotifyEquipGuideData EquipGuideData.CharacterId MessagePack round-trip");
            AssertEqual(3, roundTripGuideData.PutOnPosList.Count, "NotifyEquipGuideData EquipGuideData.PutOnPosList MessagePack round-trip count");
            AssertEqual(1, roundTripGuideData.PutOnPosList[0], "NotifyEquipGuideData EquipGuideData.PutOnPosList first position");
            AssertEqual(3, roundTripGuideData.PutOnPosList[1], "NotifyEquipGuideData EquipGuideData.PutOnPosList second position");
            AssertEqual(5, roundTripGuideData.PutOnPosList[2], "NotifyEquipGuideData EquipGuideData.PutOnPosList third position");
            AssertEmptyList(roundTripGuideData.FinishedTargets, "NotifyEquipGuideData EquipGuideData.FinishedTargets MessagePack round-trip");

            NotifyEquipChipGroupList notifyEquipChipGroupList = new()
            {
                ChipGroupDataList = []
            };
            NotifyEquipChipGroupList roundTripEquipChipGroupList = MessagePackSerializer.Deserialize<NotifyEquipChipGroupList>(
                MessagePackSerializer.Serialize(notifyEquipChipGroupList));
            AssertEmptyList(roundTripEquipChipGroupList.ChipGroupDataList, "NotifyEquipChipGroupList ChipGroupDataList MessagePack round-trip");

            Type equipOneKeyFeedRequestType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.EquipOneKeyFeedRequest");
            Type equipOneKeyFeedResponseType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.EquipOneKeyFeedResponse");
            object equipOneKeyFeedRequest = Activator.CreateInstance(equipOneKeyFeedRequestType)
                ?? throw new InvalidDataException("EquipOneKeyFeedRequest: expected a public parameterless constructor for MessagePack.");
            SetRequiredIntegerMember(equipOneKeyFeedRequest, "TargetBreakthrough", 2);
            SetRequiredIntegerMember(equipOneKeyFeedRequest, "EquipId", 7001);
            SetRequiredIntegerMember(equipOneKeyFeedRequest, "TargetLevel", 45);
            SetRequiredMemberValue(equipOneKeyFeedRequest, "OperationInfos", CreateEquipOneKeyFeedOperationInfos(equipOneKeyFeedRequestType));

            object roundTripEquipOneKeyFeedRequest = MessagePackRoundTrip(equipOneKeyFeedRequestType, equipOneKeyFeedRequest);
            AssertEqual(2, GetRequiredIntegerMember(roundTripEquipOneKeyFeedRequest, "TargetBreakthrough"), "EquipOneKeyFeedRequest TargetBreakthrough MessagePack round-trip");
            AssertEqual(7001, GetRequiredIntegerMember(roundTripEquipOneKeyFeedRequest, "EquipId"), "EquipOneKeyFeedRequest EquipId MessagePack round-trip");
            AssertEqual(45, GetRequiredIntegerMember(roundTripEquipOneKeyFeedRequest, "TargetLevel"), "EquipOneKeyFeedRequest TargetLevel MessagePack round-trip");
            AssertEquipOneKeyFeedOperationInfos(roundTripEquipOneKeyFeedRequest);

            object equipOneKeyFeedResponse = Activator.CreateInstance(equipOneKeyFeedResponseType)
                ?? throw new InvalidDataException("EquipOneKeyFeedResponse: expected a public parameterless constructor for MessagePack.");
            SetRequiredIntegerMember(equipOneKeyFeedResponse, "Code", 0);
            SetRequiredIntegerMember(equipOneKeyFeedResponse, "Breakthrough", 2);
            SetRequiredIntegerMember(equipOneKeyFeedResponse, "Level", 45);
            SetRequiredIntegerMember(equipOneKeyFeedResponse, "Exp", 320);
            SetRequiredIntegerMember(equipOneKeyFeedResponse, "SuccessTimes", 3);

            object roundTripEquipOneKeyFeedResponse = MessagePackRoundTrip(equipOneKeyFeedResponseType, equipOneKeyFeedResponse);
            AssertEqual(0, GetRequiredIntegerMember(roundTripEquipOneKeyFeedResponse, "Code"), "EquipOneKeyFeedResponse Code MessagePack round-trip");
            AssertEqual(2, GetRequiredIntegerMember(roundTripEquipOneKeyFeedResponse, "Breakthrough"), "EquipOneKeyFeedResponse Breakthrough MessagePack round-trip");
            AssertEqual(45, GetRequiredIntegerMember(roundTripEquipOneKeyFeedResponse, "Level"), "EquipOneKeyFeedResponse Level MessagePack round-trip");
            AssertEqual(320, GetRequiredIntegerMember(roundTripEquipOneKeyFeedResponse, "Exp"), "EquipOneKeyFeedResponse Exp MessagePack round-trip");
            AssertEqual(3, GetRequiredIntegerMember(roundTripEquipOneKeyFeedResponse, "SuccessTimes"), "EquipOneKeyFeedResponse SuccessTimes MessagePack round-trip");
            ValidateEquipOneKeyFeedBehavior(equipOneKeyFeedRequestType, equipOneKeyFeedResponseType);

            MethodInfo characterFromUid = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                nameof(AscNet.Common.Database.Character.FromUid),
                BindingFlags.Static | BindingFlags.Public,
                [typeof(long)]);
            MethodInfo normalizeEquips = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                nameof(AscNet.Common.Database.Character.NormalizeEquipsForCurrentTables),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo characterSave = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                nameof(AscNet.Common.Database.Character.Save),
                BindingFlags.Instance | BindingFlags.Public);
            AssertCallResultFeedsConditionalBranch(characterFromUid, normalizeEquips, "Character.FromUid equip normalization mutation guard");
            AssertCallPrecedes(characterFromUid, normalizeEquips, characterSave, "Character.FromUid equip normalization before persistence");
            AssertCallIsConditionallyGuarded(characterFromUid, characterSave, "Character.FromUid saves only changed normalized equips");

            Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            MethodInfo doLogin = RequiredMethod(
                accountModule,
                "DoLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            MethodInfo buildNotifyLogin = RequiredMethod(
                accountModule,
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            MethodInfo sendLoginState = RequiredMethod(
                accountModule,
                "SendLoginState",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            Type commandType = RequiredAscNetGameServerType("AscNet.GameServer.Commands.Command");
            Type equipCommandType = RequiredAscNetGameServerType("AscNet.GameServer.Commands.EquipCommand");
            MethodInfo equipCommandExecute = RequiredMethod(
                equipCommandType,
                "Execute",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo equipSyncHelper = RequiredMethod(
                equipCommandType,
                "SyncEquipsFromDatabase",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo loginHandler = GetRegisteredRequestHandlerMethod("LoginRequest");
            AssertCallPrecedes(loginHandler, characterFromUid, doLogin, "LoginRequestHandler normalized character load before login pushes");

            MethodInfo tableParse = RequiredMethod(
                typeof(TableReaderV2),
                nameof(TableReaderV2.Parse),
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo sendPush = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendPush),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 1);
            MethodInfo sendResponse = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendResponse),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 2);
            MethodInfo equipOneKeyFeedHandler = GetRegisteredRequestHandlerMethod("EquipOneKeyFeedRequest");
            AssertMethodDoesNotTransitivelyCallGenericMethod(doLogin, tableParse, typeof(EquipTable), "AccountModule.DoLogin table-wide equip fabrication");
            AssertMethodDoesNotTransitivelyCallGenericMethod(doLogin, sendPush, typeof(NotifyEquipDataList), "AccountModule.DoLogin incremental equip push");
            AssertMethodDoesNotTransitivelyCallGenericMethod(equipSyncHelper, tableParse, typeof(EquipTable), "EquipCommand sync table-wide phantom cleanup");
            AssertMethodTransitivelyCallsGenericMethod(doLogin, sendPush, typeof(NotifyEquipChipGroupList), "AccountModule.DoLogin equip chip group startup push");
            AssertMethodTransitivelyCallsGenericMethod(doLogin, sendPush, typeof(NotifyEquipChipAutoRecycleSite), "AccountModule.DoLogin equip chip auto-recycle startup push");
            AssertMethodTransitivelyCallsGenericMethod(doLogin, sendPush, typeof(NotifyEquipGuideData), "AccountModule.DoLogin equip guide startup push");
            AssertMethodTransitivelyCallsGenericMethod(equipOneKeyFeedHandler, sendPush, typeof(NotifyArchiveEquip), "EquipOneKeyFeedRequestHandler archive equip push");
            AssertMethodTransitivelyCallsGenericMethod(equipOneKeyFeedHandler, sendPush, typeof(NotifyItemDataList), "EquipOneKeyFeedRequestHandler consumed item push");
            AssertMethodTransitivelyCallsGenericMethod(equipOneKeyFeedHandler, sendPush, typeof(NotifyEquipDataList), "EquipOneKeyFeedRequestHandler updated and deleted equip push");
            AssertMethodTransitivelyCallsGenericMethod(equipOneKeyFeedHandler, sendResponse, equipOneKeyFeedResponseType, "EquipOneKeyFeedRequestHandler final enhancement response");

            FieldInfo commandSession = commandType.GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(commandType.FullName, "session");
            FieldInfo sessionPlayer = typeof(Session).GetField(nameof(Session.player), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(Session).FullName, nameof(Session.player));
            FieldInfo sessionCharacter = typeof(Session).GetField(nameof(Session.character), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(Session).FullName, nameof(Session.character));
            MethodInfo characterEquipsGetter = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                $"get_{nameof(AscNet.Common.Database.Character.Equips)}",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo playerDataGetter = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                $"get_{nameof(AscNet.Common.Database.Player.PlayerData)}",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo playerDataIdGetter = RequiredMethod(
                typeof(PlayerData),
                $"get_{nameof(PlayerData.Id)}",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo notifyLoginEquipListSetter = RequiredMethod(
                typeof(NotifyLogin),
                $"set_{nameof(NotifyLogin.EquipList)}",
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(List<EquipData>)]);
            AssertSessionCharacterEquipsFeedsCall(
                buildNotifyLogin,
                notifyLoginEquipListSetter,
                sessionCharacter,
                characterEquipsGetter,
                "AccountModule.BuildNotifyLogin NotifyLogin.EquipList source");
            AssertEquipCommandSyncContract(
                equipCommandType,
                equipCommandExecute,
                equipSyncHelper,
                sendLoginState,
                commandSession,
                sessionPlayer,
                sessionCharacter,
                playerDataGetter,
                playerDataIdGetter,
                characterFromUid,
                "EquipCommand sync");
        }

        private static void ValidateStageBookmarkCompatibilityShape()
        {
            GetStageBookmarkResponse response = new();
            GetStageBookmarkResponse roundTrip = MessagePackSerializer.Deserialize<GetStageBookmarkResponse>(MessagePackSerializer.Serialize(response));

            AssertEqual(0, roundTrip.Code, "GetStageBookmarkResponse Code");
            AssertEmptyList(roundTrip.StageBookmarkList, "GetStageBookmarkResponse StageBookmarkList");
            AssertEmptyList(roundTrip.BookmarkList, "GetStageBookmarkResponse BookmarkList");
            ValidateRequestHandlerRegistration("GetStageBookmarkRequest");
        }

        private static void ValidateMainLine2UpdateExhibitionChapterCompatibility()
        {
            MainLine2UpdateExhibitionChapterResponse response = new()
            {
                Code = 0
            };
            MainLine2UpdateExhibitionChapterResponse roundTrip = MessagePackSerializer.Deserialize<MainLine2UpdateExhibitionChapterResponse>(
                MessagePackSerializer.Serialize(response));

            AssertEqual(0, roundTrip.Code, "MainLine2UpdateExhibitionChapterResponse Code");
            ValidateRequestHandlerRegistration("MainLine2UpdateExhibitionChapterRequest");
        }

        private static void ValidateMainLineTreasureRewardCompatibility()
        {
            const int treasureId = 1001002;
            const int requiredStars = 12;
            const int rewardId = 1001002;
            int[] expectedChapterStages =
            [
                10010101,
                10010102,
                10010103,
                10010104,
                10010201,
                10010202,
                10010203,
                10010204,
                10010301,
                10010302,
                10010303,
                10010304
            ];

            TreasureTable treasureTable = TableReaderV2.Parse<TreasureTable>().SingleOrDefault(treasure => treasure.TreasureId == treasureId)
                ?? throw new InvalidDataException($"TreasureTable: missing current mainline treasure {treasureId}.");
            AssertEqual(requiredStars, treasureTable.RequireStar, $"TreasureTable {treasureId} RequireStar");
            AssertEqual(rewardId, treasureTable.RewardId, $"TreasureTable {treasureId} RewardId");

            ChapterTable chapterTable = TableReaderV2.Parse<ChapterTable>().SingleOrDefault(chapter => chapter.TreasureId.Contains(treasureId))
                ?? throw new InvalidDataException($"ChapterTable: no current mainline chapter maps TreasureId {treasureId}.");
            AssertEqual(1001, chapterTable.ChapterId, $"ChapterTable TreasureId {treasureId} ChapterId");
            if (!chapterTable.StageId.Contains(expectedChapterStages[0]))
                throw new InvalidDataException($"ChapterTable {chapterTable.ChapterId}: expected TreasureId {treasureId} stages to include {expectedChapterStages[0]}.");
            if (!expectedChapterStages.SequenceEqual(chapterTable.StageId))
                throw new InvalidDataException($"ChapterTable {chapterTable.ChapterId}: expected TreasureId {treasureId} stages [{string.Join(", ", expectedChapterStages)}], got [{string.Join(", ", chapterTable.StageId)}].");

            MethodInfo tableParse = RequiredMethod(
                typeof(TableReaderV2),
                nameof(TableReaderV2.Parse),
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo addTreasure = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                nameof(AscNet.Common.Database.Player.AddTreasure),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int)]);
            MethodInfo playerSave = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                nameof(AscNet.Common.Database.Player.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo inventorySave = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                nameof(AscNet.Common.Database.Inventory.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo characterSave = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                nameof(AscNet.Common.Database.Character.Save),
                BindingFlags.Instance | BindingFlags.Public);
            Type rewardHandlerType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
            MethodInfo getRewardGoods = RequiredMethod(
                rewardHandlerType,
                "GetRewardGoods",
                BindingFlags.Static | BindingFlags.Public,
                [typeof(int)]);
            MethodInfo starsMarkGetter = RequiredMethod(
                typeof(StageDatum),
                $"get_{nameof(StageDatum.StarsMark)}",
                BindingFlags.Instance | BindingFlags.Public);

            MethodInfo treasureRewardHandler = GetRegisteredRequestHandlerMethod("ReceiveTreasureRewardRequest");
            AssertEqual("HandleReceiveTreasureRewardRequestHandler", treasureRewardHandler.Name, "ReceiveTreasureRewardRequest registered handler method");
            AssertMethodTransitivelyCallsGenericMethod(treasureRewardHandler, tableParse, typeof(TreasureTable), "ReceiveTreasureRewardRequestHandler treasure table lookup");
            AssertMethodTransitivelyCallsGenericMethod(treasureRewardHandler, tableParse, typeof(ChapterTable), "ReceiveTreasureRewardRequestHandler chapter table lookup");
            AssertMethodTransitivelyCalls(treasureRewardHandler, starsMarkGetter, "ReceiveTreasureRewardRequestHandler chapter star count source");
            AssertMethodTransitivelyCalls(treasureRewardHandler, addTreasure, "ReceiveTreasureRewardRequestHandler treasure claim marker");
            AssertMethodTransitivelyCalls(treasureRewardHandler, getRewardGoods, "ReceiveTreasureRewardRequestHandler reward goods resolution");
            AssertMethodTransitivelyCalls(treasureRewardHandler, playerSave, "ReceiveTreasureRewardRequestHandler player persistence");
            AssertMethodTransitivelyCalls(treasureRewardHandler, inventorySave, "ReceiveTreasureRewardRequestHandler inventory persistence");
            AssertMethodTransitivelyCalls(treasureRewardHandler, characterSave, "ReceiveTreasureRewardRequestHandler character persistence");
        }

        private static void ValidatePlayerCostTimeUploadCompatibility()
        {
            PlayerCostTimeUploadRequest request = new()
            {
                FunctionId = 1101,
                CostTime = 42
            };
            PlayerCostTimeUploadRequest requestRoundTrip = MessagePackSerializer.Deserialize<PlayerCostTimeUploadRequest>(
                MessagePackSerializer.Serialize(request));

            AssertEqual(1101, requestRoundTrip.FunctionId, "PlayerCostTimeUploadRequest FunctionId");
            AssertEqual(42, requestRoundTrip.CostTime, "PlayerCostTimeUploadRequest CostTime");

            PlayerCostTimeUploadResponse response = new()
            {
                Code = 0
            };
            PlayerCostTimeUploadResponse responseRoundTrip = MessagePackSerializer.Deserialize<PlayerCostTimeUploadResponse>(
                MessagePackSerializer.Serialize(response));

            AssertEqual(0, responseRoundTrip.Code, "PlayerCostTimeUploadResponse Code");
            ValidateRequestHandlerRegistration("PlayerCostTimeUploadRequest");
        }

        private static void ValidateRecordPlayerPointCompatibility()
        {
            RecordPlayerPointRequest request = new()
            {
                PointId = 31001,
                PointType = 2
            };
            RecordPlayerPointRequest requestRoundTrip = MessagePackSerializer.Deserialize<RecordPlayerPointRequest>(
                MessagePackSerializer.Serialize(request));

            AssertEqual(31001, requestRoundTrip.PointId, "RecordPlayerPointRequest PointId");
            AssertEqual(2, requestRoundTrip.PointType, "RecordPlayerPointRequest PointType");

            RecordPlayerPointResponse response = new()
            {
                Code = 0
            };
            RecordPlayerPointResponse responseRoundTrip = MessagePackSerializer.Deserialize<RecordPlayerPointResponse>(
                MessagePackSerializer.Serialize(response));

            AssertEqual(0, responseRoundTrip.Code, "RecordPlayerPointResponse Code");
            ValidateRequestHandlerRegistration("RecordPlayerPointRequest");
        }

        private static void ValidatePlayerGenderCompatibility()
        {
            const int selectedGender = 2;
            const int currentClientGender = 3;
            const long changeGenderTime = 1_720_000_123;
            const int firstSetupRewardCount = 50;

            PropertyInfo playerDataGender = typeof(PlayerData).GetProperty(nameof(PlayerData.Gender), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(typeof(PlayerData).FullName, nameof(PlayerData.Gender));
            AssertEqual(typeof(long), playerDataGender.PropertyType, "PlayerData Gender type");
            MethodInfo playerDataGenderGetter = playerDataGender.GetMethod
                ?? throw new MissingMethodException(typeof(PlayerData).FullName, $"get_{nameof(PlayerData.Gender)}");
            MethodInfo playerDataGenderSetter = playerDataGender.SetMethod
                ?? throw new MissingMethodException(typeof(PlayerData).FullName, $"set_{nameof(PlayerData.Gender)}");

            PropertyInfo playerDataChangeGenderTime = typeof(PlayerData).GetProperty(nameof(PlayerData.ChangeGenderTime), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(typeof(PlayerData).FullName, nameof(PlayerData.ChangeGenderTime));
            AssertEqual(typeof(long), playerDataChangeGenderTime.PropertyType, "PlayerData ChangeGenderTime type");
            MethodInfo playerDataChangeGenderTimeGetter = playerDataChangeGenderTime.GetMethod
                ?? throw new MissingMethodException(typeof(PlayerData).FullName, $"get_{nameof(PlayerData.ChangeGenderTime)}");
            MethodInfo playerDataChangeGenderTimeSetter = playerDataChangeGenderTime.SetMethod
                ?? throw new MissingMethodException(typeof(PlayerData).FullName, $"set_{nameof(PlayerData.ChangeGenderTime)}");

            PlayerData playerData = new()
            {
                Id = 9,
                Name = "GenderCompatibilityCommandant",
                Gender = selectedGender,
                ChangeGenderTime = changeGenderTime
            };
            PlayerData playerDataRoundTrip = MessagePackSerializer.Deserialize<PlayerData>(
                MessagePackSerializer.Serialize(playerData));
            AssertEqual((long)selectedGender, playerDataRoundTrip.Gender, "PlayerData Gender MessagePack round-trip");
            AssertEqual(changeGenderTime, playerDataRoundTrip.ChangeGenderTime, "PlayerData ChangeGenderTime MessagePack round-trip");

            MethodInfo createPlayer = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                "Create",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(long)]);
            AssertMethodTransitivelyCalls(createPlayer, playerDataGenderSetter, "Player.Create new-player gender availability");

            FieldInfo requestGender = typeof(ChangePlayerGenderRequest).GetField(nameof(ChangePlayerGenderRequest.Gender), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(ChangePlayerGenderRequest).FullName, nameof(ChangePlayerGenderRequest.Gender));
            AssertEqual(typeof(int), requestGender.FieldType, "ChangePlayerGenderRequest Gender type");
            ChangePlayerGenderRequest request = new()
            {
                Gender = currentClientGender
            };
            ChangePlayerGenderRequest requestRoundTrip = MessagePackSerializer.Deserialize<ChangePlayerGenderRequest>(
                MessagePackSerializer.Serialize(request));
            AssertEqual(currentClientGender, requestRoundTrip.Gender, "ChangePlayerGenderRequest current-client gender MessagePack round-trip");

            FieldInfo responseCode = typeof(ChangePlayerGenderResponse).GetField(nameof(ChangePlayerGenderResponse.Code), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(ChangePlayerGenderResponse).FullName, nameof(ChangePlayerGenderResponse.Code));
            FieldInfo responseGender = typeof(ChangePlayerGenderResponse).GetField(nameof(ChangePlayerGenderResponse.Gender), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(ChangePlayerGenderResponse).FullName, nameof(ChangePlayerGenderResponse.Gender));
            FieldInfo responseChangeGenderTime = typeof(ChangePlayerGenderResponse).GetField(nameof(ChangePlayerGenderResponse.ChangeGenderTime), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(ChangePlayerGenderResponse).FullName, nameof(ChangePlayerGenderResponse.ChangeGenderTime));
            FieldInfo responseNextCanChangeTime = typeof(ChangePlayerGenderResponse).GetField(nameof(ChangePlayerGenderResponse.NextCanChangeTime), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(ChangePlayerGenderResponse).FullName, nameof(ChangePlayerGenderResponse.NextCanChangeTime));
            FieldInfo responsePlayerData = typeof(ChangePlayerGenderResponse).GetField(nameof(ChangePlayerGenderResponse.PlayerData), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(ChangePlayerGenderResponse).FullName, nameof(ChangePlayerGenderResponse.PlayerData));
            FieldInfo responseRewardGoodsList = typeof(ChangePlayerGenderResponse).GetField(nameof(ChangePlayerGenderResponse.RewardGoodsList), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(ChangePlayerGenderResponse).FullName, nameof(ChangePlayerGenderResponse.RewardGoodsList));
            AssertEqual(typeof(int), responseCode.FieldType, "ChangePlayerGenderResponse Code type");
            AssertEqual(typeof(long), responseGender.FieldType, "ChangePlayerGenderResponse Gender type");
            AssertEqual(typeof(long), responseChangeGenderTime.FieldType, "ChangePlayerGenderResponse ChangeGenderTime type");
            AssertEqual(typeof(long), responseNextCanChangeTime.FieldType, "ChangePlayerGenderResponse NextCanChangeTime type");
            AssertEqual(typeof(PlayerData), responsePlayerData.FieldType, "ChangePlayerGenderResponse PlayerData type");
            AssertEqual(typeof(List<RewardGoods>), responseRewardGoodsList.FieldType, "ChangePlayerGenderResponse RewardGoodsList type");
            ChangePlayerGenderResponse response = new()
            {
                Gender = selectedGender,
                ChangeGenderTime = changeGenderTime,
                NextCanChangeTime = changeGenderTime,
                PlayerData = playerData
            };
            response.RewardGoodsList.Add(new RewardGoods()
            {
                RewardType = (int)RewardType.Item,
                TemplateId = AscNet.Common.Database.Inventory.FreeGem,
                Count = firstSetupRewardCount
            });
            ChangePlayerGenderResponse responseRoundTrip = MessagePackSerializer.Deserialize<ChangePlayerGenderResponse>(
                MessagePackSerializer.Serialize(response));
            AssertEqual((long)selectedGender, responseRoundTrip.Gender, "ChangePlayerGenderResponse Gender MessagePack round-trip");
            AssertEqual(changeGenderTime, responseRoundTrip.ChangeGenderTime, "ChangePlayerGenderResponse ChangeGenderTime MessagePack round-trip");
            AssertEqual(changeGenderTime, responseRoundTrip.NextCanChangeTime, "ChangePlayerGenderResponse NextCanChangeTime MessagePack round-trip");
            if (responseRoundTrip.PlayerData is null)
                throw new InvalidDataException("ChangePlayerGenderResponse PlayerData MessagePack round-trip: expected player data.");
            AssertEqual((long)selectedGender, responseRoundTrip.PlayerData.Gender, "ChangePlayerGenderResponse PlayerData.Gender MessagePack round-trip");
            AssertEqual(changeGenderTime, responseRoundTrip.PlayerData.ChangeGenderTime, "ChangePlayerGenderResponse PlayerData.ChangeGenderTime MessagePack round-trip");
            AssertEqual(1, responseRoundTrip.RewardGoodsList.Count, "ChangePlayerGenderResponse RewardGoodsList MessagePack count");
            AssertEqual((int)RewardType.Item, responseRoundTrip.RewardGoodsList[0].RewardType, "ChangePlayerGenderResponse RewardGoodsList reward type");
            AssertEqual(AscNet.Common.Database.Inventory.FreeGem, responseRoundTrip.RewardGoodsList[0].TemplateId, "ChangePlayerGenderResponse RewardGoodsList Black Card item id");
            AssertEqual(firstSetupRewardCount, responseRoundTrip.RewardGoodsList[0].Count, "ChangePlayerGenderResponse RewardGoodsList Black Card count");

            FieldInfo notifyGender = typeof(NotifyPlayerGender).GetField(nameof(NotifyPlayerGender.Gender), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(NotifyPlayerGender).FullName, nameof(NotifyPlayerGender.Gender));
            FieldInfo notifyChangeGenderTime = typeof(NotifyPlayerGender).GetField(nameof(NotifyPlayerGender.ChangeGenderTime), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(NotifyPlayerGender).FullName, nameof(NotifyPlayerGender.ChangeGenderTime));
            AssertEqual(typeof(long), notifyGender.FieldType, "NotifyPlayerGender Gender type");
            AssertEqual(typeof(long), notifyChangeGenderTime.FieldType, "NotifyPlayerGender ChangeGenderTime type");
            NotifyPlayerGender notify = new()
            {
                Gender = selectedGender,
                ChangeGenderTime = changeGenderTime
            };
            NotifyPlayerGender notifyRoundTrip = MessagePackSerializer.Deserialize<NotifyPlayerGender>(
                MessagePackSerializer.Serialize(notify));
            AssertEqual((long)selectedGender, notifyRoundTrip.Gender, "NotifyPlayerGender Gender MessagePack round-trip");
            AssertEqual(changeGenderTime, notifyRoundTrip.ChangeGenderTime, "NotifyPlayerGender ChangeGenderTime MessagePack round-trip");

            MethodInfo playerDataGetter = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                $"get_{nameof(AscNet.Common.Database.Player.PlayerData)}",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo playerSave = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                nameof(AscNet.Common.Database.Player.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo inventoryDo = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                nameof(AscNet.Common.Database.Inventory.Do),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int), typeof(int)]);
            MethodInfo inventorySave = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                nameof(AscNet.Common.Database.Inventory.Save),
                BindingFlags.Instance | BindingFlags.Public);

            MethodInfo changeGenderHandler = GetRegisteredRequestHandlerMethod("ChangePlayerGenderRequest");
            AssertEqual("ChangePlayerGenderRequestHandler", changeGenderHandler.Name, "ChangePlayerGenderRequest registered handler method");
            AssertGenderValidationRejectsOnlyOutsideCurrentClientRange(
                changeGenderHandler,
                requestGender,
                responseCode,
                "ChangePlayerGenderRequestHandler current-client gender validation");
            AssertHandlerSendsResponseCode(
                changeGenderHandler,
                responseCode,
                20002021,
                "ChangePlayerGenderRequestHandler unchanged gender response");
            AssertSameGenderResponseRequiresAlreadySetGender(
                changeGenderHandler,
                requestGender,
                playerDataGenderGetter,
                playerDataChangeGenderTimeGetter,
                responseCode,
                "ChangePlayerGenderRequestHandler unchanged gender guard");
            AssertRequestFieldFeedsSetterBeforePersistence(
                changeGenderHandler,
                requestGender,
                playerDataGenderSetter,
                playerSave,
                "ChangePlayerGenderRequestHandler selected gender persistence");
            AssertLiveGenderRefreshBeforeSuccessResponse(
                changeGenderHandler,
                playerDataGetter,
                playerDataGenderGetter,
                playerDataGenderSetter,
                playerDataChangeGenderTimeGetter,
                playerDataChangeGenderTimeSetter,
                responseGender,
                responseChangeGenderTime,
                responseNextCanChangeTime,
                responsePlayerData,
                notifyGender,
                notifyChangeGenderTime,
                "ChangePlayerGenderRequestHandler live gender refresh");
            AssertFirstGenderSetupRewardPath(
                changeGenderHandler,
                playerDataChangeGenderTimeGetter,
                playerDataChangeGenderTimeSetter,
                responseRewardGoodsList,
                inventoryDo,
                inventorySave,
                playerSave,
                "ChangePlayerGenderRequestHandler first gender setup reward");
        }

        private static void ValidateBoardMutualClientPushCompatibility()
        {
            AssertEqual(true, Session.IsKnownClientPush("BoardMutualRequest"), "Session known client push BoardMutualRequest");
            AssertEqual(false, Session.IsKnownClientPush("DefinitelyUnknownClientPushForCompatibilityTest"), "Session unknown client push");
        }

        private static void ValidateCharacterProgressionPersistenceCompatibility()
        {
            MethodInfo characterSave = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                "Save",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo inventorySave = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                "Save",
                BindingFlags.Instance | BindingFlags.Public);

            PersistenceHandlerContract[] characterProgressionHandlers =
            [
                new("CharacterLevelUpRequest", "CharacterLevelUpRequestHandler"),
                new("CharacterPromoteGradeRequest", "CharacterPromoteGradeRequestHandler"),
                new("CharacterActivateStarRequest", "CharacterActivateStarRequestHandler"),
                new("CharacterPromoteQualityRequest", "CharacterPromoteQualityRequestHandler"),
                new("CharacterUnlockSkillGroupRequest", "CharacterUnlockSkillGroupRequestHandler"),
                new("CharacterUpgradeSkillGroupRequest", "CharacterUpgradeSkillGroupRequestHandler"),
                new("CharacterUnlockEnhanceSkillRequest", "CharacterUnlockEnhanceSkillRequestHandler"),
                new("CharacterUpgradeEnhanceSkillRequest", "CharacterUpgradeEnhanceSkillRequestHandler"),
                new("CharacterExchangeRequest", "CharacterExchangeRequestHandler")
            ];

            foreach (PersistenceHandlerContract handlerContract in characterProgressionHandlers)
            {
                MethodInfo handler = GetRegisteredRequestHandlerMethod(handlerContract.RequestName);
                AssertEqual(handlerContract.HandlerMethodName, handler.Name, $"{handlerContract.RequestName} registered handler method");
                AssertMethodTransitivelyCalls(handler, characterSave, $"{handlerContract.HandlerMethodName} character persistence");
                AssertMethodTransitivelyCalls(handler, inventorySave, $"{handlerContract.HandlerMethodName} inventory persistence");
            }

            MethodInfo fightSettleHandler = GetRegisteredRequestHandlerMethod("FightSettleRequest");
            AssertEqual("FightSettleRequestHandler", fightSettleHandler.Name, "FightSettleRequest registered handler method");
            AssertMethodTransitivelyCalls(fightSettleHandler, characterSave, "FightSettleRequestHandler character card-exp persistence");
        }

        private static void ValidateExpLevelCompatibility()
        {
            const int lotusCharacterId = 1021001;
            const int mainlineStageId = 10010101;

            AscNet.Common.Database.CharacterLevelUpTemplate levelOneTemplate = RequiredCharacterLevelUpTemplate(lotusCharacterId, level: 1);
            AscNet.Common.Database.CharacterLevelUpTemplate levelTwoTemplate = RequiredCharacterLevelUpTemplate(lotusCharacterId, level: 2);
            AscNet.Common.Database.CharacterLevelUpTemplate levelThreeTemplate = RequiredCharacterLevelUpTemplate(lotusCharacterId, level: 3);
            StageTable firstMainlineStage = TableReaderV2.Parse<StageTable>().Single(stage => stage.StageId == mainlineStageId);

            AssertEqual<int?>(null, firstMainlineStage.TeamExp, $"StageTable {mainlineStageId} legacy TeamExp");
            AssertEqual(6, firstMainlineStage.FirstTeamExp ?? throw new InvalidDataException($"StageTable {mainlineStageId}: missing FirstTeamExp."), $"StageTable {mainlineStageId} FirstTeamExp");
            AssertEqual<int?>(null, firstMainlineStage.CardExp, $"StageTable {mainlineStageId} legacy CardExp");
            int firstClearCardExp = firstMainlineStage.FirstCardExp
                ?? throw new InvalidDataException($"StageTable {mainlineStageId}: missing FirstCardExp.");
            AssertEqual(11, firstClearCardExp, $"StageTable {mainlineStageId} FirstCardExp");

            AscNet.Common.Database.Character thresholdNoExpRoster = CreateTestCharacterRoster(lotusCharacterId, level: 1);
            CharacterData thresholdNoExpCharacter = RequiredCharacterData(thresholdNoExpRoster, lotusCharacterId);
            thresholdNoExpCharacter.Exp = (uint)levelOneTemplate.Exp;
            CharacterData thresholdNoExpResult = thresholdNoExpRoster.AddCharacterExp(lotusCharacterId, exp: 0)
                ?? throw new InvalidDataException("AddCharacterExp returned nil for an owned character at the level threshold.");
            AssertEqual(2, thresholdNoExpResult.Level, "AddCharacterExp threshold rollover with zero gained exp level");
            AssertEqual(0U, thresholdNoExpResult.Exp, "AddCharacterExp threshold rollover with zero gained exp carried exp");

            AscNet.Common.Database.Character thresholdBattleExpRoster = CreateTestCharacterRoster(lotusCharacterId, level: 1);
            CharacterData thresholdBattleExpCharacter = RequiredCharacterData(thresholdBattleExpRoster, lotusCharacterId);
            thresholdBattleExpCharacter.Exp = (uint)levelOneTemplate.Exp;
            CharacterData thresholdBattleExpResult = thresholdBattleExpRoster.AddCharacterExp(lotusCharacterId, firstClearCardExp)
                ?? throw new InvalidDataException("AddCharacterExp returned nil for an owned character receiving first-clear battle exp.");
            AssertEqual(2, thresholdBattleExpResult.Level, "AddCharacterExp threshold rollover with battle exp level");
            AssertEqual((uint)firstClearCardExp, thresholdBattleExpResult.Exp, "AddCharacterExp threshold rollover with battle exp carried exp");

            if (levelThreeTemplate.Exp <= 1)
                throw new InvalidDataException($"CharacterLevelUpTemplate: expected level 3 exp threshold to support a positive carry-over assertion, got {levelThreeTemplate.Exp}.");
            int multiLevelCarryExp = Math.Max(1, Math.Min(firstClearCardExp, levelThreeTemplate.Exp - 1));
            AscNet.Common.Database.Character multiLevelRoster = CreateTestCharacterRoster(lotusCharacterId, level: 1);
            CharacterData multiLevelResult = multiLevelRoster.AddCharacterExp(lotusCharacterId, exp: levelOneTemplate.Exp + levelTwoTemplate.Exp + multiLevelCarryExp, maxLvl: 10)
                ?? throw new InvalidDataException("AddCharacterExp returned nil for an owned character receiving enough exp for multiple levels.");
            AssertEqual(3, multiLevelResult.Level, "AddCharacterExp multi-level rollover below commandant cap level");
            AssertEqual((uint)multiLevelCarryExp, multiLevelResult.Exp, "AddCharacterExp multi-level rollover below commandant cap carried exp");

            AscNet.Common.Database.Character cappedRoster = CreateTestCharacterRoster(lotusCharacterId, level: 1);
            CharacterData cappedCharacter = RequiredCharacterData(cappedRoster, lotusCharacterId);
            cappedCharacter.Exp = (uint)levelOneTemplate.Exp;
            CharacterData cappedResult = cappedRoster.AddCharacterExp(lotusCharacterId, exp: levelTwoTemplate.Exp + firstClearCardExp, maxLvl: 2)
                ?? throw new InvalidDataException("AddCharacterExp returned nil for an owned character capped by commandant level.");
            AssertEqual(2, cappedResult.Level, "AddCharacterExp maxLvl cap level");
            AssertEqual((uint)levelTwoTemplate.Exp, cappedResult.Exp, "AddCharacterExp maxLvl cap exp");

            MethodInfo fightSettleHandler = GetRegisteredRequestHandlerMethod("FightSettleRequest");
            AssertEqual("FightSettleRequestHandler", fightSettleHandler.Name, "FightSettleRequest registered handler method");

            MethodInfo expSanityCheck = RequiredMethod(
                typeof(SessionExtensions),
                nameof(SessionExtensions.ExpSanityCheck),
                BindingFlags.Static | BindingFlags.Public,
                [typeof(Session)]);
            MethodInfo playerSave = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                nameof(AscNet.Common.Database.Player.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo inventorySave = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                nameof(AscNet.Common.Database.Inventory.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo inventoryDo = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                nameof(AscNet.Common.Database.Inventory.Do),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int), typeof(int)]);
            MethodInfo characterSave = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                nameof(AscNet.Common.Database.Character.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo stageSave = RequiredMethod(
                typeof(AscNet.Common.Database.Stage),
                nameof(AscNet.Common.Database.Stage.Save),
                BindingFlags.Instance | BindingFlags.Public);

            MethodInfo levelUpHandler = GetRegisteredRequestHandlerMethod("CharacterLevelUpRequest");
            AssertEqual("CharacterLevelUpRequestHandler", levelUpHandler.Name, "CharacterLevelUpRequest registered handler method");
            AssertLevelUpMaxCapResponsePrecedesInventoryMutation(levelUpHandler, inventoryDo, inventorySave, "CharacterLevelUpRequestHandler commandant cap guard");

            AssertMethodTransitivelyCalls(fightSettleHandler, expSanityCheck, "FightSettleRequestHandler commandant exp sanity settlement");
            AssertMethodTransitivelyCalls(fightSettleHandler, playerSave, "FightSettleRequestHandler player settlement persistence");
            AssertMethodTransitivelyCalls(fightSettleHandler, inventorySave, "FightSettleRequestHandler inventory settlement persistence");
            AssertMethodTransitivelyCalls(fightSettleHandler, characterSave, "FightSettleRequestHandler character settlement persistence");
            AssertMethodTransitivelyCalls(fightSettleHandler, stageSave, "FightSettleRequestHandler stage settlement persistence");
        }

        private static AscNet.Common.Database.Character CreateTestCharacterRoster(int characterId, int level)
        {
            AscNet.Common.Database.Character roster = new()
            {
                Uid = 1,
                Characters = [],
                Equips = [],
                Fashions = []
            };

            roster.AddCharacter((uint)characterId, level);
            return roster;
        }

        private static CharacterData RequiredCharacterData(AscNet.Common.Database.Character roster, int characterId)
        {
            return roster.Characters.SingleOrDefault(character => character.Id == characterId)
                ?? throw new InvalidDataException($"Character roster is missing character {characterId}.");
        }

        private static AscNet.Common.Database.CharacterLevelUpTemplate RequiredCharacterLevelUpTemplate(int characterId, int level)
        {
            CharacterTable characterTable = TableReaderV2.Parse<CharacterTable>().Single(character => character.Id == characterId);
            return AscNet.Common.Database.Character.characterLevelUpTemplates
                .SingleOrDefault(template => template.Level == level && template.Type == characterTable.Type)
                ?? throw new InvalidDataException($"CharacterLevelUpTemplate: missing level {level}, type {characterTable.Type} for character {characterId}.");
        }

        private static void ValidatePr2QualityCompatibility()
        {
            PlayerData playerData = new()
            {
                Id = 7,
                Name = "CompatibilityCommandant",
                NewPlayerTaskActiveDay = 4
            };
            PlayerData playerDataRoundTrip = MessagePackSerializer.Deserialize<PlayerData>(
                MessagePackSerializer.Serialize(playerData));
            AssertEqual(4, playerDataRoundTrip.NewPlayerTaskActiveDay, "PlayerData NewPlayerTaskActiveDay MessagePack round-trip");

            NotifyNewPlayerTaskStatus taskStatus = new()
            {
                NewPlayerTaskActiveDay = 4
            };
            NotifyNewPlayerTaskStatus taskStatusRoundTrip = MessagePackSerializer.Deserialize<NotifyNewPlayerTaskStatus>(
                MessagePackSerializer.Serialize(taskStatus));
            AssertEqual(4, taskStatusRoundTrip.NewPlayerTaskActiveDay, "NotifyNewPlayerTaskStatus NewPlayerTaskActiveDay MessagePack round-trip");

            AscNet.Common.Database.Player player = new();
            AssertEqual(1, player.GatherRewards.Count, "Player initial gather reward count");
            AssertEqual(5, player.GatherRewards[0], "Player initial gather reward id");
            AssertEqual(false, player.AddGatherReward(5), "Player AddGatherReward rejects the already-claimed base reward");
            AssertEqual(1, player.GatherRewards.Count, "Player duplicate base gather reward count");
            AssertEqual(true, player.AddGatherReward(6), "Player AddGatherReward accepts a new reward id");
            AssertEqual(false, player.AddGatherReward(6), "Player AddGatherReward rejects a duplicate new reward id");
            AssertEqual(2, player.GatherRewards.Count, "Player idempotent gather reward count");

            MethodInfo sendPush = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendPush),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 1);
            MethodInfo addGatherReward = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                nameof(AscNet.Common.Database.Player.AddGatherReward),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int)]);
            MethodInfo playerSave = RequiredMethod(
                typeof(AscNet.Common.Database.Player),
                nameof(AscNet.Common.Database.Player.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo inventorySave = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                nameof(AscNet.Common.Database.Inventory.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo characterSave = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                nameof(AscNet.Common.Database.Character.Save),
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo changeNameTimeSetter = typeof(PlayerData).GetProperty(nameof(PlayerData.ChangeNameTime))?.SetMethod
                ?? throw new MissingMethodException(typeof(PlayerData).FullName, $"set_{nameof(PlayerData.ChangeNameTime)}");
            MethodInfo newPlayerTaskActiveDaySetter = typeof(PlayerData).GetProperty(nameof(PlayerData.NewPlayerTaskActiveDay))?.SetMethod
                ?? throw new MissingMethodException(typeof(PlayerData).FullName, $"set_{nameof(PlayerData.NewPlayerTaskActiveDay)}");
            Type rewardHandlerType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
            MethodInfo getRewardGoods = RequiredMethod(
                rewardHandlerType,
                "GetRewardGoods",
                BindingFlags.Static | BindingFlags.Public,
                [typeof(int)]);

            MethodInfo loginHandler = GetRegisteredRequestHandlerMethod("LoginRequest");
            AssertEqual("LoginRequestHandler", loginHandler.Name, "LoginRequest registered handler method");
            AssertMethodTransitivelyCalls(loginHandler, addGatherReward, "LoginRequestHandler base gather reward claim");
            AssertMethodTransitivelyCalls(loginHandler, newPlayerTaskActiveDaySetter, "LoginRequestHandler new-player active-day update");
            AssertMethodTransitivelyCallsGenericMethod(loginHandler, sendPush, typeof(NotifyGatherRewardList), "LoginRequestHandler gather reward list push");
            AssertMethodTransitivelyCallsGenericMethod(loginHandler, sendPush, typeof(NotifyBirthdayPlot), "LoginRequestHandler birthday plot push");
            AssertMethodTransitivelyCallsGenericMethod(loginHandler, sendPush, typeof(NotifyNewPlayerTaskStatus), "LoginRequestHandler new-player task status push");

            MethodInfo changeNameHandler = GetRegisteredRequestHandlerMethod("ChangePlayerNameRequest");
            AssertEqual("ChangePlayerNameRequestHandler", changeNameHandler.Name, "ChangePlayerNameRequest registered handler method");
            AssertMethodTransitivelyCalls(changeNameHandler, changeNameTimeSetter, "ChangePlayerNameRequestHandler ChangeNameTime update");

            MethodInfo changeBirthdayHandler = GetRegisteredRequestHandlerMethod("ChangePlayerBirthdayRequest");
            AssertEqual("ChangePlayerBirthdayRequestHandler", changeBirthdayHandler.Name, "ChangePlayerBirthdayRequest registered handler method");
            AssertMethodTransitivelyCallsGenericMethod(changeBirthdayHandler, sendPush, typeof(NotifyBirthdayPlot), "ChangePlayerBirthdayRequestHandler birthday plot push");

            MethodInfo gatherRewardHandler = GetRegisteredRequestHandlerMethod("GatherRewardRequest");
            AssertEqual("HandleGatherRewardRequestHandler", gatherRewardHandler.Name, "GatherRewardRequest registered handler method");
            AssertMethodTransitivelyCalls(gatherRewardHandler, addGatherReward, "GatherRewardRequest duplicate-safe claim marker");
            AssertCallResultFeedsConditionalBranch(gatherRewardHandler, addGatherReward, "GatherRewardRequest duplicate-safe claim guard");
            AssertMethodTransitivelyCalls(gatherRewardHandler, getRewardGoods, "GatherRewardRequest reward goods resolution");
            AssertMethodTransitivelyCalls(gatherRewardHandler, playerSave, "GatherRewardRequest player persistence");
            AssertMethodTransitivelyCalls(gatherRewardHandler, inventorySave, "GatherRewardRequest inventory persistence");
            AssertMethodTransitivelyCalls(gatherRewardHandler, characterSave, "GatherRewardRequest character persistence");
        }

        private static void ValidateStoryCourseRewardCompatibility()
        {
            const int storyStageId = 10010103;
            const int staleStageFirstRewardId = 10010103;
            const int expectedCourseRewardId = 300000;
            const int expectedCourseShowId = 1031001;
            const int expectedRewardGoodsTemplateId = 1031001;
            const int expectedStoryTaskIdForStage = 102;
            const int currentStoryTaskProgressStageId = 10010104;
            const int expectedCurrentStoryTaskId = 103;

            List<CourseTable> courseRows = TableReaderV2.Parse<CourseTable>()
                .Where(course => course.StageId == storyStageId)
                .ToList();
            AssertEqual(1, courseRows.Count, $"CourseTable rows for StageId {storyStageId}");

            CourseTable courseTable = courseRows[0];
            AssertEqual(expectedCourseRewardId, courseTable.RewardId, $"CourseTable {storyStageId} RewardId");
            AssertEqual(expectedCourseShowId, courseTable.ShowId, $"CourseTable {storyStageId} ShowId");

            StageTable stageTable = TableReaderV2.Parse<StageTable>().FirstOrDefault(stage => stage.StageId == storyStageId)
                ?? throw new InvalidDataException($"StageTable: missing current story stage {storyStageId}.");
            int stageFirstRewardId = stageTable.FirstRewardId
                ?? throw new InvalidDataException($"StageTable {storyStageId}: missing FirstRewardId.");
            AssertEqual(staleStageFirstRewardId, stageFirstRewardId, $"StageTable {storyStageId} stale FirstRewardId");
            if (stageFirstRewardId == courseTable.RewardId)
                throw new InvalidDataException($"CourseTable {storyStageId}: RewardId must not be taken from StageTable.FirstRewardId.");

            List<RewardGoodsTable> rewardGoodsTables = TableReaderV2.Parse<RewardGoodsTable>();
            List<RewardGoodsTable> courseRewardGoods = ResolveRewardGoods(courseTable.RewardId, rewardGoodsTables, $"CourseTable {storyStageId} RewardId");
            if (!courseRewardGoods.Any(goods => goods.TemplateId == expectedRewardGoodsTemplateId))
                throw new InvalidDataException($"CourseTable {storyStageId}: expected RewardId {courseTable.RewardId} to resolve to RewardGoods template {expectedRewardGoodsTemplateId}.");

            List<RewardGoodsTable> staleFirstRewardGoods = ResolveRewardGoods(stageFirstRewardId, rewardGoodsTables, $"StageTable {storyStageId} FirstRewardId");
            if (staleFirstRewardGoods.Any(goods => goods.TemplateId == expectedRewardGoodsTemplateId))
                throw new InvalidDataException($"StageTable {storyStageId}: stale FirstRewardId path unexpectedly resolves to course RewardGoods template {expectedRewardGoodsTemplateId}.");

            ValidateStoryTaskProgressCompatibility(storyStageId, expectedStoryTaskIdForStage);
            ValidateStoryTaskProgressCompatibility(currentStoryTaskProgressStageId, expectedCurrentStoryTaskId);

            MethodInfo stageSave = RequiredMethod(
                typeof(AscNet.Common.Database.Stage),
                "Save",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo inventorySave = RequiredMethod(
                typeof(AscNet.Common.Database.Inventory),
                "Save",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo characterSave = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                "Save",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo stageAddCourse = RequiredMethod(
                typeof(AscNet.Common.Database.Stage),
                "AddCourse",
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(uint)]);
            MethodInfo stageAddFinishedTask = RequiredMethod(
                typeof(AscNet.Common.Database.Stage),
                "AddFinishedTask",
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int)]);
            MethodInfo tableParse = RequiredMethod(
                typeof(TableReaderV2),
                nameof(TableReaderV2.Parse),
                BindingFlags.Static | BindingFlags.Public);
            Type taskModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule");
            MethodInfo sendStoryTaskSync = RequiredMethod(
                taskModule,
                "SendStoryTaskSync",
                BindingFlags.Static | BindingFlags.Public,
                [typeof(Session)]);

            MethodInfo courseRewardHandler = GetRegisteredRequestHandlerMethod("GetCourseRewardRequest");
            AssertEqual("GetCourseRewardRequestHandler", courseRewardHandler.Name, "GetCourseRewardRequest registered handler method");
            AssertMethodTransitivelyCallsGenericMethod(courseRewardHandler, tableParse, typeof(CourseTable), "GetCourseRewardRequestHandler course table lookup");
            AssertMethodTransitivelyCallsGenericMethod(courseRewardHandler, tableParse, typeof(RewardTable), "GetCourseRewardRequestHandler reward table lookup");
            AssertMethodTransitivelyCallsGenericMethod(courseRewardHandler, tableParse, typeof(RewardGoodsTable), "GetCourseRewardRequestHandler reward goods lookup");
            AssertMethodDoesNotTransitivelyCallGenericMethod(courseRewardHandler, tableParse, typeof(StageTable), "GetCourseRewardRequestHandler stale stage first-clear lookup");
            AssertMethodTransitivelyCalls(courseRewardHandler, stageAddCourse, "GetCourseRewardRequestHandler course claim marker");
            AssertMethodTransitivelyCalls(courseRewardHandler, stageSave, "GetCourseRewardRequestHandler stage course persistence");
            AssertMethodTransitivelyCalls(courseRewardHandler, inventorySave, "GetCourseRewardRequestHandler inventory reward persistence");
            AssertMethodTransitivelyCalls(courseRewardHandler, characterSave, "GetCourseRewardRequestHandler character reward persistence");

            MethodInfo finishTaskHandler = GetRegisteredRequestHandlerMethod("FinishTaskRequest");
            AssertEqual("FinishTaskRequestHandler", finishTaskHandler.Name, "FinishTaskRequest registered handler method");
            AssertMethodTransitivelyCallsGenericMethod(finishTaskHandler, tableParse, typeof(StoryTaskTable), "FinishTaskRequestHandler story task lookup");
            AssertMethodTransitivelyCallsGenericMethod(finishTaskHandler, tableParse, typeof(StoryTaskConditionTable), "FinishTaskRequestHandler story task condition lookup");
            AssertMethodTransitivelyCallsGenericMethod(finishTaskHandler, tableParse, typeof(RewardTable), "FinishTaskRequestHandler reward table lookup");
            AssertMethodTransitivelyCallsGenericMethod(finishTaskHandler, tableParse, typeof(RewardGoodsTable), "FinishTaskRequestHandler reward goods lookup");
            AssertMethodTransitivelyCalls(finishTaskHandler, stageAddFinishedTask, "FinishTaskRequestHandler finished task marker");
            AssertMethodTransitivelyCalls(finishTaskHandler, stageSave, "FinishTaskRequestHandler stage task persistence");
            AssertMethodTransitivelyCalls(finishTaskHandler, inventorySave, "FinishTaskRequestHandler inventory reward persistence");
            AssertMethodTransitivelyCalls(finishTaskHandler, characterSave, "FinishTaskRequestHandler character reward persistence");
            AssertMethodTransitivelyCalls(finishTaskHandler, sendStoryTaskSync, "FinishTaskRequestHandler story task sync push");

            MethodInfo finishMultiTaskHandler = GetRegisteredRequestHandlerMethod("FinishMultiTaskRequest");
            AssertEqual("FinishMultiTaskRequestHandler", finishMultiTaskHandler.Name, "FinishMultiTaskRequest registered handler method");
            AssertMethodTransitivelyCallsGenericMethod(finishMultiTaskHandler, tableParse, typeof(StoryTaskTable), "FinishMultiTaskRequestHandler story task lookup");
            AssertMethodTransitivelyCallsGenericMethod(finishMultiTaskHandler, tableParse, typeof(StoryTaskConditionTable), "FinishMultiTaskRequestHandler story task condition lookup");
            AssertMethodTransitivelyCallsGenericMethod(finishMultiTaskHandler, tableParse, typeof(RewardTable), "FinishMultiTaskRequestHandler reward table lookup");
            AssertMethodTransitivelyCallsGenericMethod(finishMultiTaskHandler, tableParse, typeof(RewardGoodsTable), "FinishMultiTaskRequestHandler reward goods lookup");
            AssertMethodTransitivelyCalls(finishMultiTaskHandler, stageAddFinishedTask, "FinishMultiTaskRequestHandler finished task marker");
            AssertMethodTransitivelyCalls(finishMultiTaskHandler, stageSave, "FinishMultiTaskRequestHandler stage task persistence");
            AssertMethodTransitivelyCalls(finishMultiTaskHandler, inventorySave, "FinishMultiTaskRequestHandler inventory reward persistence");
            AssertMethodTransitivelyCalls(finishMultiTaskHandler, characterSave, "FinishMultiTaskRequestHandler character reward persistence");
            AssertMethodTransitivelyCalls(finishMultiTaskHandler, sendStoryTaskSync, "FinishMultiTaskRequestHandler story task sync push");

            MethodInfo fightSettleHandler = GetRegisteredRequestHandlerMethod("FightSettleRequest");
            AssertEqual("FightSettleRequestHandler", fightSettleHandler.Name, "FightSettleRequest registered handler method");
            AssertMethodDoesNotTransitivelyCall(fightSettleHandler, stageAddCourse, "FightSettleRequestHandler course claim marker");
        }

        private static List<RewardGoodsTable> ResolveRewardGoods(int rewardId, IReadOnlyCollection<RewardGoodsTable> rewardGoodsTables, string name)
        {
            RewardTable rewardTable = TableReaderV2.Parse<RewardTable>().FirstOrDefault(reward => reward.Id == rewardId)
                ?? throw new InvalidDataException($"{name}: missing RewardTable id {rewardId}.");
            if (rewardTable.SubIds.Count == 0)
                throw new InvalidDataException($"{name}: RewardTable {rewardId} has no SubIds.");

            HashSet<int> rewardSubIds = rewardTable.SubIds.ToHashSet();
            List<RewardGoodsTable> rewardGoods = rewardGoodsTables
                .Where(goods => rewardSubIds.Contains(goods.Id))
                .ToList();
            int[] missingRewardGoodsIds = rewardSubIds.Except(rewardGoods.Select(goods => goods.Id)).ToArray();
            if (missingRewardGoodsIds.Length > 0)
                throw new InvalidDataException($"{name}: missing RewardGoods rows for SubIds {string.Join(", ", missingRewardGoodsIds)}.");

            return rewardGoods;
        }

        private static void ValidateStoryTaskProgressCompatibility(int passedStageId, int expectedStoryTaskId)
        {
            const int taskStateAchieved = 3;
            const int taskStateFinish = 4;

            StoryTaskConditionTable storyTaskCondition = TableReaderV2.Parse<StoryTaskConditionTable>()
                .Single(condition => condition.Params.Contains(passedStageId));
            AssertEqual(expectedStoryTaskId, storyTaskCondition.Id, $"StoryTaskCondition stage {passedStageId} Id");

            StoryTaskTable storyTask = TableReaderV2.Parse<StoryTaskTable>()
                .Single(task => task.Condition == storyTaskCondition.Id);
            AssertEqual(expectedStoryTaskId, storyTask.Id, $"StoryTask stage {passedStageId} task id");
            AssertEqual(1, storyTask.Result, $"StoryTask {expectedStoryTaskId} required progress");

            Session session = CreateStoryTaskProgressSession(passedStageId);
            LoginTask achievedTask = RequiredStoryLoginTask(BuildStoryTaskData(session), expectedStoryTaskId);
            AssertEqual(taskStateAchieved, achievedTask.State, $"BuildStoryTaskData task {expectedStoryTaskId} achieved state");
            AssertEqual(1, achievedTask.Schedule.Count, $"BuildStoryTaskData task {expectedStoryTaskId} schedule count");
            AssertEqual((uint)storyTaskCondition.Id, achievedTask.Schedule[0].Id, $"BuildStoryTaskData task {expectedStoryTaskId} condition id");
            AssertEqual(storyTask.Result, achievedTask.Schedule[0].Value, $"BuildStoryTaskData task {expectedStoryTaskId} progress value");

            if (!session.stage.AddFinishedTask(expectedStoryTaskId))
                throw new InvalidDataException($"Stage failed to mark story task {expectedStoryTaskId} finished.");

            LoginTask finishedTask = RequiredStoryLoginTask(BuildStoryTaskData(session), expectedStoryTaskId);
            AssertEqual(taskStateFinish, finishedTask.State, $"BuildStoryTaskData task {expectedStoryTaskId} finished state");
            AssertEqual(storyTask.Result, finishedTask.Schedule[0].Value, $"BuildStoryTaskData task {expectedStoryTaskId} finished progress value");
        }

        private static Session CreateStoryTaskProgressSession(int passedStageId)
        {
            Session session = (Session)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Session));
            session.stage = new AscNet.Common.Database.Stage
            {
                Uid = -1,
                Stages = new(),
                Course = new(),
                FinishedTasks = new()
            };
            session.stage.AddStage(new StageDatum
            {
                StageId = passedStageId,
                StarsMark = 7,
                Passed = true,
                PassTimesToday = 0,
                PassTimesTotal = 1
            });
            return session;
        }

        private static List<LoginTask> BuildStoryTaskData(Session session)
        {
            Type taskModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule");
            MethodInfo buildStoryTaskData = RequiredMethod(
                taskModule,
                "BuildStoryTaskData",
                BindingFlags.Static | BindingFlags.Public,
                [typeof(Session)]);

            return (List<LoginTask>?)buildStoryTaskData.Invoke(null, [session])
                ?? throw new InvalidDataException("TaskModule.BuildStoryTaskData returned nil.");
        }

        private static LoginTask RequiredStoryLoginTask(IEnumerable<LoginTask> tasks, int taskId)
        {
            return tasks.SingleOrDefault(task => task.Id == (uint)taskId)
                ?? throw new InvalidDataException($"BuildStoryTaskData did not include story task {taskId}.");
        }

        private sealed record PersistenceHandlerContract(string RequestName, string HandlerMethodName);

        private static void ValidateBossSingleLoginCompatibilityShape()
        {
            NotifyFubenBossSingleData notification = new()
            {
                FubenBossSingleData = new()
                {
                    ActivityNo = 1,
                    TotalScore = 0,
                    MaxScore = 0,
                    OldLevelType = 1,
                    LevelType = 1,
                    ChallengeCount = 0,
                    RemainTime = 3600 * 24,
                    AutoFightCount = 0,
                    RankPlatform = 0,
                    AfreshId = 0,
                    ChallengeLevelType = 0,
                    IsResetOpen = false
                }
            };

            NotifyFubenBossSingleData roundTrip = MessagePackSerializer.Deserialize<NotifyFubenBossSingleData>(
                MessagePackSerializer.Serialize(notification));

            NotifyFubenBossSingleData.NotifyFubenBossSingleDataFubenBossSingleData bossSingleData = roundTrip.FubenBossSingleData
                ?? throw new InvalidDataException("NotifyFubenBossSingleData FubenBossSingleData serialized as nil.");
            AssertEmptyList(bossSingleData.CharacterPoints, "NotifyFubenBossSingleData FubenBossSingleData.CharacterPoints");
            AssertEmptyList(bossSingleData.HistoryList, "NotifyFubenBossSingleData FubenBossSingleData.HistoryList");
            AssertEmptyList(bossSingleData.RewardIds, "NotifyFubenBossSingleData FubenBossSingleData.RewardIds");
            AssertEmptyList(bossSingleData.BossList, "NotifyFubenBossSingleData FubenBossSingleData.BossList");
            AssertEmptyList(bossSingleData.TrialStageInfoList, "NotifyFubenBossSingleData FubenBossSingleData.TrialStageInfoList");
            AssertEmptyList(bossSingleData.BestiraryStageInfoList, "NotifyFubenBossSingleData FubenBossSingleData.BestiraryStageInfoList");
            AssertEmptyList(bossSingleData.ChallengeStageHistoryList, "NotifyFubenBossSingleData FubenBossSingleData.ChallengeStageHistoryList");
            AssertEmptyList(bossSingleData.StageRecordList, "NotifyFubenBossSingleData FubenBossSingleData.StageRecordList");
            AssertEqual(false, bossSingleData.IsResetOpen, "NotifyFubenBossSingleData FubenBossSingleData.IsResetOpen");
            AssertEqual(0, bossSingleData.AfreshId, "NotifyFubenBossSingleData FubenBossSingleData.AfreshId");
            AssertEqual(1, bossSingleData.LevelType, "NotifyFubenBossSingleData FubenBossSingleData.LevelType");
            AssertEqual(1, bossSingleData.OldLevelType, "NotifyFubenBossSingleData FubenBossSingleData.OldLevelType");
            if (bossSingleData.RemainTime == 0)
                throw new InvalidDataException("NotifyFubenBossSingleData FubenBossSingleData.RemainTime: expected a positive value.");
        }

        private static void ValidateCurrentClientGuideTableCompatibility()
        {
            List<GuideGroupTable> guideGroups = TableReaderV2.Parse<GuideGroupTable>();
            if (guideGroups.Count <= 500)
                throw new InvalidDataException($"GuideGroupTable: expected materially more than the stale 241-row table, got {guideGroups.Count} rows.");

            int[] requiredGuideGroupIds = [100004, 64764, 64772];
            foreach (int guideGroupId in requiredGuideGroupIds)
            {
                if (!guideGroups.Any(guideGroup => guideGroup.Id == guideGroupId))
                    throw new InvalidDataException($"GuideGroupTable: missing current-client guide group id {guideGroupId}.");
            }

            List<GuideFightTable> guideFights = TableReaderV2.Parse<GuideFightTable>();
            if (!guideFights.Any(guideFight => guideFight.StageId == 10010005))
                throw new InvalidDataException("GuideFightTable: missing current tutorial stage 10010005.");
        }

        private static void ValidateRequestHandlerRegistration(string requestName)
        {
            Dictionary<string, RequestPacketHandlerDelegate> handlersSnapshot = new(PacketFactory.ReqHandlers);

            try
            {
                PacketFactory.ReqHandlers.Remove(requestName);
                PacketFactory.LoadPacketHandlers();

                RequestPacketHandlerDelegate? handler = PacketFactory.GetRequestPacketHandler(requestName);
                if (handler is null)
                    throw new InvalidDataException($"PacketFactory did not register {requestName}.");
            }
            finally
            {
                PacketFactory.ReqHandlers.Clear();
                foreach (KeyValuePair<string, RequestPacketHandlerDelegate> handler in handlersSnapshot)
                    PacketFactory.ReqHandlers.Add(handler.Key, handler.Value);
            }
        }

        private static MethodInfo GetRegisteredRequestHandlerMethod(string requestName)
        {
            Dictionary<string, RequestPacketHandlerDelegate> handlersSnapshot = new(PacketFactory.ReqHandlers);

            try
            {
                PacketFactory.ReqHandlers.Remove(requestName);
                PacketFactory.LoadPacketHandlers();

                RequestPacketHandlerDelegate? handler = PacketFactory.GetRequestPacketHandler(requestName);
                if (handler is null)
                    throw new InvalidDataException($"PacketFactory did not register {requestName}.");

                return handler.Method;
            }
            finally
            {
                PacketFactory.ReqHandlers.Clear();
                foreach (KeyValuePair<string, RequestPacketHandlerDelegate> handler in handlersSnapshot)
                    PacketFactory.ReqHandlers.Add(handler.Key, handler.Value);
            }
        }

        private static RequestPacketHandlerDelegate GetRegisteredRequestHandler(string requestName)
        {
            Dictionary<string, RequestPacketHandlerDelegate> handlersSnapshot = new(PacketFactory.ReqHandlers);

            try
            {
                PacketFactory.ReqHandlers.Remove(requestName);
                PacketFactory.LoadPacketHandlers();

                return PacketFactory.GetRequestPacketHandler(requestName)
                    ?? throw new InvalidDataException($"PacketFactory did not register {requestName}.");
            }
            finally
            {
                PacketFactory.ReqHandlers.Clear();
                foreach (KeyValuePair<string, RequestPacketHandlerDelegate> handler in handlersSnapshot)
                    PacketFactory.ReqHandlers.Add(handler.Key, handler.Value);
            }
        }

        private static void InvokeRegisteredRequestHandler(string requestName, Session session, int packetId, object? request)
        {
            RequestPacketHandlerDelegate handler = GetRegisteredRequestHandler(requestName);
            Packet.Request packet = new()
            {
                Id = packetId,
                Name = requestName,
                Content = request is null ? [] : MessagePackSerialize(request.GetType(), request)
            };

            try
            {
                handler.Invoke(session, packet);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"{requestName}: registered handler invocation failed.", exception);
            }
        }

        private static TResponse ReadResponsePayload<TResponse>(LoopbackSessionHarness harness, int expectedPacketId, string expectedResponseName, string name)
        {
            Packet packet = harness.ReadPacket(name);
            AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} packet type");
            Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
            AssertEqual(expectedPacketId, response.Id, $"{name} packet id");
            AssertEqual(expectedResponseName, response.Name, $"{name} packet name");
            return MessagePackSerializer.Deserialize<TResponse>(response.Content);
        }

        private static MethodInfo RequiredMethod(Type declaringType, string name, BindingFlags bindingFlags)
        {
            return RequiredMethod(declaringType, name, bindingFlags, Type.EmptyTypes);
        }

        private static MethodInfo RequiredMethod(Type declaringType, string name, BindingFlags bindingFlags, Type[] parameterTypes)
        {
            return declaringType.GetMethod(name, bindingFlags, binder: null, types: parameterTypes, modifiers: null)
                ?? throw new MissingMethodException(declaringType.FullName, name);
        }

        private static MethodInfo RequiredGenericMethodDefinition(Type declaringType, string name, BindingFlags bindingFlags, int parameterCount)
        {
            MethodInfo[] matches = declaringType.GetMethods(bindingFlags)
                .Where(method => method.Name == name && method.IsGenericMethodDefinition && method.GetParameters().Length == parameterCount)
                .ToArray();

            return matches.Length switch
            {
                1 => matches[0],
                0 => throw new MissingMethodException(declaringType.FullName, name),
                _ => throw new AmbiguousMatchException($"{declaringType.FullName}.{name} has {matches.Length} generic overloads with {parameterCount} parameters.")
            };
        }

        private static Type RequiredAscNetGameServerType(string fullName)
        {
            return typeof(PacketFactory).Assembly.GetType(fullName, throwOnError: true)
                ?? throw new TypeLoadException(fullName);
        }

        private static void AssertMethodTransitivelyCalls(MethodInfo method, MethodInfo target, string name)
        {
            if (!CallsTargetTransitively(method, target, []))
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to call {target.DeclaringType?.FullName}.{target.Name} directly or through a private helper.");
        }

        private static void AssertMethodDoesNotTransitivelyCall(MethodInfo method, MethodInfo target, string name)
        {
            if (CallsTargetTransitively(method, target, []))
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} not to call {target.DeclaringType?.FullName}.{target.Name} directly or through a private helper.");
        }

        private static void AssertMethodTransitivelyCallsGenericMethod(MethodInfo method, MethodInfo genericMethodDefinition, Type genericArgument, string name)
        {
            if (!CallsGenericTargetTransitively(method, genericMethodDefinition, genericArgument, []))
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to call {FormatGenericMethod(genericMethodDefinition, genericArgument)} directly or through a private helper.");
        }

        private static void AssertMethodDoesNotTransitivelyCallGenericMethod(MethodInfo method, MethodInfo genericMethodDefinition, Type genericArgument, string name)
        {
            if (CallsGenericTargetTransitively(method, genericMethodDefinition, genericArgument, []))
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} not to call {FormatGenericMethod(genericMethodDefinition, genericArgument)} directly or through a private helper.");
        }

        private static bool CallsGenericTargetTransitively(MethodInfo method, MethodInfo genericMethodDefinition, Type genericArgument, HashSet<string> visited)
        {
            if (!visited.Add(MethodKey(method)))
                return false;

            foreach (MethodBase calledMethod in CalledMethods(method))
            {
                if (GenericMethodsMatch(calledMethod, genericMethodDefinition, genericArgument))
                    return true;

                if (calledMethod is MethodInfo nestedMethod && CanTraversePrivateHelper(method, nestedMethod) && CallsGenericTargetTransitively(nestedMethod, genericMethodDefinition, genericArgument, visited))
                    return true;
            }

            return false;
        }

        private static bool CallsTargetTransitively(MethodInfo method, MethodInfo target, HashSet<string> visited)
        {
            if (!visited.Add(MethodKey(method)))
                return false;

            foreach (MethodBase calledMethod in CalledMethods(method))
            {
                if (MethodsMatch(calledMethod, target))
                    return true;

                if (calledMethod is MethodInfo nestedMethod && CanTraversePrivateHelper(method, nestedMethod) && CallsTargetTransitively(nestedMethod, target, visited))
                    return true;
            }

            return false;
        }

        private static bool CanTraversePrivateHelper(MethodInfo method, MethodInfo candidate)
        {
            Type? methodType = method.DeclaringType;
            Type? candidateType = candidate.DeclaringType;
            if (methodType is null || candidateType is null)
                return false;

            bool sameTypeHelper = candidateType == methodType;
            bool generatedClosureHelper = candidateType.DeclaringType == methodType
                || methodType.DeclaringType == candidateType
                || (methodType.DeclaringType is not null && methodType.DeclaringType == candidateType.DeclaringType);
            return sameTypeHelper
                ? candidate.IsPrivate
                : generatedClosureHelper && !candidate.IsPublic;
        }

        private static void AssertCallResultFeedsConditionalBranch(MethodInfo method, MethodInfo target, string name)
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray()
                ?? throw new InvalidDataException($"{method.DeclaringType?.FullName}.{method.Name}: method body is unavailable.");
            Module module = method.Module;
            Type[] typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
            Type[] methodArguments = method.GetGenericArguments();

            for (int offset = 0; offset < il.Length;)
            {
                OpCode opCode = ReadOpCode(il, ref offset);
                if (opCode.OperandType == OperandType.InlineMethod)
                {
                    int metadataToken = BitConverter.ToInt32(il, offset);
                    offset += 4;

                    MethodBase? calledMethod;
                    try
                    {
                        calledMethod = module.ResolveMethod(metadataToken, typeArguments, methodArguments);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    if (calledMethod is not null && MethodsMatch(calledMethod, target))
                    {
                        if (NextMeaningfulOpCodeIsConditionalBranch(il, offset))
                            return;

                        throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to branch on {target.DeclaringType?.FullName}.{target.Name}'s return value.");
                    }
                }
                else
                {
                    offset += OperandSize(opCode.OperandType, il, offset);
                }
            }

            throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to call {target.DeclaringType?.FullName}.{target.Name}.");
        }

        private static void AssertCallPrecedes(MethodInfo method, MethodInfo firstTarget, MethodInfo secondTarget, string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();
            int firstIndex = FindCallIndex(instructions, firstTarget, startIndex: 0);
            if (firstIndex < 0)
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to call {firstTarget.DeclaringType?.FullName}.{firstTarget.Name}.");

            int secondIndex = FindCallIndex(instructions, secondTarget, startIndex: firstIndex + 1);
            if (secondIndex < 0)
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to call {secondTarget.DeclaringType?.FullName}.{secondTarget.Name} after {firstTarget.DeclaringType?.FullName}.{firstTarget.Name}.");

            int firstReturnIndex = instructions.FindIndex(instruction => instruction.OpCode.FlowControl == FlowControl.Return);
            if (firstReturnIndex >= 0 && firstReturnIndex < firstIndex)
                throw new InvalidDataException($"{name}: expected {firstTarget.DeclaringType?.FullName}.{firstTarget.Name} before the first return.");
        }

        private static void AssertCallIsConditionallyGuarded(MethodInfo method, MethodInfo target, string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();
            int callIndex = FindCallIndex(instructions, target, startIndex: 0);
            if (callIndex < 0)
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to call {target.DeclaringType?.FullName}.{target.Name}.");
            if (!HasConditionalBranchGuard(instructions, callIndex, callIndex))
                throw new InvalidDataException($"{name}: expected {target.DeclaringType?.FullName}.{target.Name} to be reached through a conditional branch.");
        }

        private static void AssertSessionCharacterEquipsFeedsCall(MethodInfo method, MethodInfo consumer, FieldInfo sessionCharacter, MethodInfo characterEquipsGetter, string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();
            int consumerIndex = FindCallIndex(instructions, consumer, startIndex: 0);
            if (consumerIndex < 0)
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to call {consumer.DeclaringType?.FullName}.{consumer.Name}.");

            AssertRecentSessionCharacterEquipsSource(instructions, consumerIndex, sessionCharacter, characterEquipsGetter, name);
        }


        private static void AssertEquipDataPayloadEquals(EquipData expected, EquipData actual, string name)
        {
            AssertEqual(expected.Id, actual.Id, $"{name} instance id");
            AssertEqual(expected.TemplateId, actual.TemplateId, $"{name} template id");
            AssertEqual(expected.CharacterId, actual.CharacterId, $"{name} character id");
            AssertEqual(expected.Level, actual.Level, $"{name} level");
            AssertEqual(expected.Exp, actual.Exp, $"{name} exp");
            AssertEqual(expected.Breakthrough, actual.Breakthrough, $"{name} breakthrough");
            AssertEqual(expected.IsLock, actual.IsLock, $"{name} lock state");
            AssertEqual(expected.CreateTime, actual.CreateTime, $"{name} create time");
            AssertEqual(expected.IsRecycle, actual.IsRecycle, $"{name} recycle state");
            if (actual.ResonanceInfo is null)
                throw new InvalidDataException($"{name}: expected ResonanceInfo to round-trip as a list.");
            if (actual.UnconfirmedResonanceInfo is null)
                throw new InvalidDataException($"{name}: expected UnconfirmedResonanceInfo to round-trip as a list.");
            if (actual.AwakeSlotList is null)
                throw new InvalidDataException($"{name}: expected AwakeSlotList to round-trip as a list.");
            if (actual.WeaponOverrunData is null)
                throw new InvalidDataException($"{name}: expected WeaponOverrunData to round-trip as an object.");
            AssertEqual(expected.ResonanceInfo.Count, actual.ResonanceInfo.Count, $"{name} resonance count");
            AssertEqual(expected.UnconfirmedResonanceInfo.Count, actual.UnconfirmedResonanceInfo.Count, $"{name} unconfirmed resonance count");
            AssertEqual(expected.AwakeSlotList.Count, actual.AwakeSlotList.Count, $"{name} awake slot count");
            AssertEqual(expected.WeaponOverrunData.Level, actual.WeaponOverrunData.Level, $"{name} weapon overrun level");
            AssertEqual(expected.WeaponOverrunData.ChoseSuit, actual.WeaponOverrunData.ChoseSuit, $"{name} weapon overrun chose suit");
            AssertEqual(expected.WeaponOverrunData.ActiveSuits.Count, actual.WeaponOverrunData.ActiveSuits.Count, $"{name} weapon overrun active suit count");
        }

        private static object CreateEquipOneKeyFeedOperationInfos(Type requestType)
        {
            MemberInfo operationInfosMember = RequiredDataMember(requestType, "OperationInfos");
            Type operationInfosType = MemberValueType(operationInfosMember);
            System.Collections.IList operationInfos = CreateListInstance(operationInfosType, "EquipOneKeyFeedRequest.OperationInfos");
            Type operationInfoType = RequiredListElementType(operationInfosType, "EquipOneKeyFeedRequest.OperationInfos");

            object itemOperation = Activator.CreateInstance(operationInfoType)
                ?? throw new InvalidDataException("EquipOneKeyFeedRequest.OperationInfos item: expected a public parameterless constructor for MessagePack.");
            SetRequiredMemberValue(itemOperation, "UseEquipIdList", null);
            SetRequiredIntegerList(itemOperation, "UseItemIdList", [3001, 3002]);
            SetRequiredIntegerMember(itemOperation, "OperationType", 1);
            SetRequiredIntegerList(itemOperation, "UseItemCountList", [2, 3]);
            operationInfos.Add(itemOperation);

            object equipOperation = Activator.CreateInstance(operationInfoType)
                ?? throw new InvalidDataException("EquipOneKeyFeedRequest.OperationInfos item: expected a public parameterless constructor for MessagePack.");
            SetRequiredIntegerList(equipOperation, "UseEquipIdList", [9101, 9102]);
            SetRequiredMemberValue(equipOperation, "UseItemIdList", null);
            SetRequiredIntegerMember(equipOperation, "OperationType", 2);
            SetRequiredMemberValue(equipOperation, "UseItemCountList", null);
            operationInfos.Add(equipOperation);

            return operationInfos;
        }

        private static void AssertEquipOneKeyFeedOperationInfos(object request)
        {
            object? operationInfosValue = GetRequiredMemberValue(request, "OperationInfos");
            if (operationInfosValue is not System.Collections.IList operationInfos)
                throw new InvalidDataException("EquipOneKeyFeedRequest.OperationInfos MessagePack round-trip: expected a list.");
            AssertEqual(2, operationInfos.Count, "EquipOneKeyFeedRequest.OperationInfos MessagePack round-trip count");

            object itemOperation = operationInfos[0]
                ?? throw new InvalidDataException("EquipOneKeyFeedRequest.OperationInfos[0] MessagePack round-trip: expected operation data.");
            AssertRequiredMemberNull(itemOperation, "UseEquipIdList", "EquipOneKeyFeedRequest.OperationInfos[0].UseEquipIdList nullable MessagePack round-trip");
            AssertIntegerList([3001, 3002], GetRequiredIntegerList(itemOperation, "UseItemIdList"), "EquipOneKeyFeedRequest.OperationInfos[0].UseItemIdList MessagePack round-trip");
            AssertEqual(1, GetRequiredIntegerMember(itemOperation, "OperationType"), "EquipOneKeyFeedRequest.OperationInfos[0].OperationType MessagePack round-trip");
            AssertIntegerList([2, 3], GetRequiredIntegerList(itemOperation, "UseItemCountList"), "EquipOneKeyFeedRequest.OperationInfos[0].UseItemCountList MessagePack round-trip");

            object equipOperation = operationInfos[1]
                ?? throw new InvalidDataException("EquipOneKeyFeedRequest.OperationInfos[1] MessagePack round-trip: expected operation data.");
            AssertIntegerList([9101, 9102], GetRequiredIntegerList(equipOperation, "UseEquipIdList"), "EquipOneKeyFeedRequest.OperationInfos[1].UseEquipIdList MessagePack round-trip");
            AssertRequiredMemberNull(equipOperation, "UseItemIdList", "EquipOneKeyFeedRequest.OperationInfos[1].UseItemIdList nullable MessagePack round-trip");
            AssertEqual(2, GetRequiredIntegerMember(equipOperation, "OperationType"), "EquipOneKeyFeedRequest.OperationInfos[1].OperationType MessagePack round-trip");
            AssertRequiredMemberNull(equipOperation, "UseItemCountList", "EquipOneKeyFeedRequest.OperationInfos[1].UseItemCountList nullable MessagePack round-trip");
        }

        private static void ValidateEquipPutOnSlotSwapBehavior(IReadOnlyList<EquipTable> currentEquipRows)
        {
            Type equipModuleType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.EquipModule");
            MethodInfo isSameEquipSlot = RequiredMethod(
                equipModuleType,
                "IsSameEquipSlot",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(EquipTable), typeof(EquipTable)]);
            MethodInfo equipPutOnHandler = GetRegisteredRequestHandlerMethod("EquipPutOnRequest");
            MethodInfo sendPush = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendPush),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 1);
            MethodInfo equipDataListAddRange = RequiredMethod(
                typeof(List<EquipData>),
                nameof(List<EquipData>.AddRange),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(IEnumerable<EquipData>)]);

            AssertMethodTransitivelyCalls(equipPutOnHandler, isSameEquipSlot, "EquipPutOnRequestHandler target-table slot comparison helper");
            AssertMethodTransitivelyCallsGenericMethod(equipPutOnHandler, sendPush, typeof(NotifyEquipDataList), "EquipPutOnRequestHandler equip swap notification push");
            AssertMethodTransitivelyCalls(equipPutOnHandler, equipDataListAddRange, "EquipPutOnRequestHandler notifies all previous equips unequipped from the occupied slot");

            EquipTable[] weaponSlotRows = currentEquipRows
                .Where(equip => AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip) && equip.Type == 1)
                .Take(2)
                .ToArray();
            if (weaponSlotRows.Length < 2)
                throw new InvalidDataException("EquipPutOnRequestHandler slot swap behavior: expected at least two current-client Type 1 weapon rows.");

            AssertEqual(
                true,
                InvokeIsSameEquipSlot(isSameEquipSlot, weaponSlotRows[0], weaponSlotRows[1], "EquipPutOnRequestHandler Type 1 weapon slot helper"),
                "EquipPutOnRequestHandler Type 1 weapons share a slot");

            (EquipTable memoryTarget, EquipTable memorySameSite, EquipTable memoryDifferentSite) = SelectMemorySlotRows(currentEquipRows);
            AssertEqual(
                true,
                InvokeIsSameEquipSlot(isSameEquipSlot, memorySameSite, memoryTarget, "EquipPutOnRequestHandler memory same-site slot helper"),
                "EquipPutOnRequestHandler memory rows with same Type and Site share a slot");
            AssertEqual(
                false,
                InvokeIsSameEquipSlot(isSameEquipSlot, memoryDifferentSite, memoryTarget, "EquipPutOnRequestHandler memory different-site slot helper"),
                "EquipPutOnRequestHandler memory rows with same Type and different Site do not share a slot");

            AssertEquipPutOnUnequipsEveryExistingWeaponAndNotifies(equipPutOnHandler, currentEquipRows);
        }

        private static (EquipTable Target, EquipTable SameSite, EquipTable DifferentSite) SelectMemorySlotRows(IReadOnlyList<EquipTable> currentEquipRows)
        {
            foreach (IGrouping<int, EquipTable> typeGroup in currentEquipRows
                .Where(equip => AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip) && equip.Type is not 1 and not 99)
                .GroupBy(equip => equip.Type))
            {
                IGrouping<int, EquipTable>? sameSiteGroup = typeGroup
                    .GroupBy(equip => equip.Site)
                    .FirstOrDefault(siteGroup => siteGroup.Count() >= 2);
                if (sameSiteGroup is null)
                    continue;

                EquipTable? differentSite = typeGroup.FirstOrDefault(equip => equip.Site != sameSiteGroup.Key);
                if (differentSite is null)
                    continue;

                EquipTable[] sameSiteRows = sameSiteGroup.Take(2).ToArray();
                return (sameSiteRows[0], sameSiteRows[1], differentSite);
            }

            throw new InvalidDataException("EquipPutOnRequestHandler slot swap behavior: expected current-client memory equip rows with a same Type/Site pair and a same-Type different-Site row.");
        }

        private static bool InvokeIsSameEquipSlot(MethodInfo isSameEquipSlot, EquipTable? equippedTable, EquipTable targetTable, string name)
        {
            return (bool)(isSameEquipSlot.Invoke(null, [equippedTable, targetTable])
                ?? throw new InvalidDataException($"{name}: IsSameEquipSlot returned nil."));
        }

        private static void AssertEquipPutOnUnequipsEveryExistingWeaponAndNotifies(MethodInfo equipPutOnHandler, IReadOnlyList<EquipTable> currentEquipRows)
        {
            List<EquipTable> weaponRows = currentEquipRows
                .Where(equip => AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip) && equip.Type == 1)
                .ToList();
            if (weaponRows.Count < 3)
                throw new InvalidDataException("EquipPutOnRequestHandler behavior: expected at least three current-client Type 1 weapon rows.");
            EquipTable targetWeapon = weaponRows[0];
            EquipTable firstPreviousWeapon = weaponRows.First(weapon => weapon.Id != targetWeapon.Id);
            EquipTable secondPreviousWeapon = weaponRows.First(weapon => weapon.Id != targetWeapon.Id && weapon.Id != firstPreviousWeapon.Id);
            int? mismatchedRequestSite = currentEquipRows
                .Select(equip => (int?)equip.Site)
                .FirstOrDefault(site => site != targetWeapon.Site);
            int requestSite = mismatchedRequestSite
                ?? (targetWeapon.Site == int.MaxValue ? int.MinValue : targetWeapon.Site + 1);

            const int characterId = 1021001;
            EquipData firstPreviousEquip = CreateEquipPutOnTestEquip(92001, firstPreviousWeapon, characterId);
            EquipData secondPreviousEquip = CreateEquipPutOnTestEquip(92002, secondPreviousWeapon, characterId);
            EquipData targetEquip = CreateEquipPutOnTestEquip(92003, targetWeapon, characterId: 0);
            EquipData otherCharacterWeapon = CreateEquipPutOnTestEquip(92004, secondPreviousWeapon, characterId + 1);

            AscNet.Common.Database.Character character = new()
            {
                Uid = 92000,
                Characters = [],
                Equips = [firstPreviousEquip, secondPreviousEquip, targetEquip, otherCharacterWeapon],
                Fashions = []
            };

            using LoopbackSessionHarness harness = new(character);
            EquipPutOnRequest request = new()
            {
                CharacterId = characterId,
                EquipId = (int)targetEquip.Id,
                Site = requestSite
            };
            Packet.Request packet = new()
            {
                Id = 920,
                Name = nameof(EquipPutOnRequest),
                Content = MessagePackSerializer.Serialize(request)
            };

            try
            {
                equipPutOnHandler.Invoke(null, [harness.Session, packet]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw new InvalidDataException("EquipPutOnRequestHandler behavior: handler invocation failed.", exception.InnerException);
            }

            AssertEqual(characterId, targetEquip.CharacterId, "EquipPutOnRequestHandler behavior equipped requested Type 1 weapon");
            AssertEqual(0, firstPreviousEquip.CharacterId, "EquipPutOnRequestHandler behavior unequipped first previous Type 1 weapon despite mismatched request Site");
            AssertEqual(0, secondPreviousEquip.CharacterId, "EquipPutOnRequestHandler behavior unequipped second previous Type 1 weapon despite mismatched request Site");
            AssertEqual(characterId + 1, otherCharacterWeapon.CharacterId, "EquipPutOnRequestHandler behavior does not unequip another character's Type 1 weapon");

            Dictionary<uint, EquipTable> tableById = currentEquipRows.ToDictionary(equip => (uint)equip.Id);
            int remainingWeaponCount = character.Equips.Count(equip =>
                equip.CharacterId == characterId
                && tableById.TryGetValue(equip.TemplateId, out EquipTable? equipTable)
                && equipTable.Type == 1);
            AssertEqual(1, remainingWeaponCount, "EquipPutOnRequestHandler behavior character retains exactly one Type 1 weapon");

            Packet pushPacket = harness.ReadPacket("EquipPutOnRequestHandler NotifyEquipDataList push");
            AssertEqual(Packet.ContentType.Push, pushPacket.Type, "EquipPutOnRequestHandler first packet type");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(pushPacket.Content);
            AssertEqual(nameof(NotifyEquipDataList), push.Name, "EquipPutOnRequestHandler pushed equip data list");
            NotifyEquipDataList notifyEquipDataList = MessagePackSerializer.Deserialize<NotifyEquipDataList>(push.Content);
            AssertIntegerList(
                [firstPreviousEquip.Id, secondPreviousEquip.Id],
                notifyEquipDataList.EquipDataList.Select(equip => (long)equip.Id).ToArray(),
                "EquipPutOnRequestHandler NotifyEquipDataList contains only unequipped previous weapons");
            AssertIntegerList(
                [0, 0],
                notifyEquipDataList.EquipDataList.Select(equip => (long)equip.CharacterId).ToArray(),
                "EquipPutOnRequestHandler NotifyEquipDataList clears previous weapon character ids");

            Packet responsePacket = harness.ReadPacket("EquipPutOnRequestHandler response");
            AssertEqual(Packet.ContentType.Response, responsePacket.Type, "EquipPutOnRequestHandler second packet type");
            Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(responsePacket.Content);
            AssertEqual(nameof(EquipPutOnResponse), response.Name, "EquipPutOnRequestHandler response packet name");
            EquipPutOnResponse equipPutOnResponse = MessagePackSerializer.Deserialize<EquipPutOnResponse>(response.Content);
            AssertEqual(0, equipPutOnResponse.Code, "EquipPutOnRequestHandler successful weapon swap response");
        }

        private static EquipData CreateEquipPutOnTestEquip(uint id, EquipTable table, int characterId)
        {
            return new EquipData
            {
                Id = id,
                TemplateId = (uint)table.Id,
                CharacterId = characterId,
                Level = 1,
                Exp = 0,
                Breakthrough = 0,
                ResonanceInfo = [],
                UnconfirmedResonanceInfo = [],
                AwakeSlotList = [],
                IsLock = false,
                IsRecycle = false
            };
        }

        private sealed class LoopbackSessionHarness : IDisposable
        {
            private static readonly MessagePackSerializerOptions PacketSerializerOptions = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

            private readonly TcpListener listener;
            private readonly TcpClient clientSide;
            private readonly TcpClient sessionSide;

            public Session Session { get; }

            public LoopbackSessionHarness(
                AscNet.Common.Database.Character character,
                AscNet.Common.Database.Player? player = null,
                AscNet.Common.Database.Inventory? inventory = null,
                string sessionId = "equip-put-on-test")
            {
                listener = new TcpListener(IPAddress.Loopback, port: 0);
                listener.Start();

                clientSide = new TcpClient(AddressFamily.InterNetwork)
                {
                    NoDelay = true,
                    ReceiveTimeout = 5000
                };
                clientSide.Connect((IPEndPoint)listener.LocalEndpoint);

                sessionSide = listener.AcceptTcpClient();
                sessionSide.NoDelay = true;
                Session = new Session(sessionId, sessionSide)
                {
                    character = character,
                    player = player ?? CreateDrawCompatibilityPlayer(character.Uid),
                    inventory = inventory ?? CreateDrawCompatibilityInventory(character.Uid, [])
                };
            }

            public Packet ReadPacket(string name)
            {
                NetworkStream stream = clientSide.GetStream();
                byte[] lengthBytes = ReadExact(stream, sizeof(int), $"{name} length");
                int packetLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (packetLength <= 0 || packetLength > 1 << 20)
                    throw new InvalidDataException($"{name}: invalid packet length {packetLength}.");

                byte[] packetBytes = ReadExact(stream, packetLength, name);
                Crypto.HaruCrypt.Decrypt(packetBytes);
                return MessagePackSerializer.Deserialize<Packet>(packetBytes, PacketSerializerOptions);
            }

            public void Dispose()
            {
                clientSide.Close();
                sessionSide.Close();
                listener.Stop();
            }

            private static byte[] ReadExact(NetworkStream stream, int length, string name)
            {
                byte[] buffer = GC.AllocateUninitializedArray<byte>(length);
                int offset = 0;
                while (offset < length)
                {
                    int read;
                    try
                    {
                        read = stream.Read(buffer, offset, length - offset);
                    }
                    catch (IOException exception)
                    {
                        throw new InvalidDataException($"{name}: timed out waiting for session packet bytes.", exception);
                    }

                    if (read == 0)
                        throw new InvalidDataException($"{name}: session socket closed before {length} bytes were read.");

                    offset += read;
                }

                return buffer;
            }
        }

        private static void ValidateEquipOneKeyFeedBehavior(Type requestType, Type responseType)
        {
            Type equipModuleType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.EquipModule");
            Type operationInfoType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.EquipFeedOperationInfo");
            MethodInfo consumeFeedItems = RequiredMethod(
                equipModuleType,
                "ConsumeFeedItems",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session), typeof(List<ItemTable>), typeof(int), operationInfoType, typeof(Dictionary<int, int>)]);
            MethodInfo consumeFeedEquips = RequiredMethod(
                equipModuleType,
                "ConsumeFeedEquips",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session), typeof(EquipData), typeof(EquipTable), typeof(List<EquipTable>), typeof(List<EquipBreakThroughTable>), operationInfoType, typeof(Dictionary<int, int>), typeof(NotifyEquipDataList)]);
            MethodInfo shouldUseCogOnlyEnhancement = RequiredMethod(
                equipModuleType,
                "ShouldUseCogOnlyEnhancement",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(EquipTable)]);
            MethodInfo hasFeedMaterials = RequiredMethod(
                equipModuleType,
                "HasFeedMaterials",
                BindingFlags.Static | BindingFlags.NonPublic,
                [operationInfoType]);
            MethodInfo addItemDelta = RequiredMethod(
                equipModuleType,
                "AddItemDelta",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Dictionary<int, int>), typeof(int), typeof(int)]);
            MethodInfo applyItemDeltas = RequiredMethod(
                equipModuleType,
                "ApplyItemDeltas",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session), typeof(Dictionary<int, int>), typeof(NotifyItemDataList)]);

            List<EquipTable> equipTables = TableReaderV2.Parse<EquipTable>();
            List<EquipBreakThroughTable> equipBreakThroughTables = TableReaderV2.Parse<EquipBreakThroughTable>();
            List<ItemTable> itemTables = TableReaderV2.Parse<ItemTable>();
            ItemTable equipExpItem = itemTables
                .Where(item =>
                {
                    var upgradeInfo = item.GetEquipUpgradeInfo();
                    return upgradeInfo.Exp > 0
                        && upgradeInfo.Cost > 0
                        && item.SubTypeParams.Count > 0
                        && item.SubTypeParams[0] == 1;
                })
                .OrderBy(item => item.GetEquipUpgradeInfo().Exp)
                .FirstOrDefault()
                ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: expected a current-client weapon equip exp item with an item and coin cost.");
            var equipExpItemUpgradeInfo = equipExpItem.GetEquipUpgradeInfo();

            const int targetLevel = 2;
            var lowRarityExplicitFeedCase = FindLowRarityWeaponExplicitFeedCase();
            EquipTable lowRarityTargetEquipTable = lowRarityExplicitFeedCase.Target;
            EquipTable lowRarityFeedEquipTable = lowRarityExplicitFeedCase.Feed;
            AssertEqual(true, (bool)(shouldUseCogOnlyEnhancement.Invoke(null, [lowRarityTargetEquipTable])
                ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: ShouldUseCogOnlyEnhancement returned nil for a low-rarity target.")), "EquipOneKeyFeedRequestHandler behavior low-rarity weapon is cog-only only when no feed materials are supplied");

            const int lowRarityTargetEquipId = 7001;
            const int lowRarityFeedEquipId = 7002;
            EquipData lowRarityTargetEquip = NewEquip(lowRarityTargetEquipId, lowRarityTargetEquipTable);
            EquipData lowRarityFeedEquip = NewEquip(lowRarityFeedEquipId, lowRarityFeedEquipTable);
            AscNet.Common.Database.Character lowRarityCharacter = NewCharacter(9001, lowRarityTargetEquip, lowRarityFeedEquip);
            Session lowRaritySession = NewSession(lowRarityCharacter, NewInventory(lowRarityCharacter.Uid));
            object lowRarityOperationInfo = NewOperationInfo([lowRarityFeedEquipId], null, null);
            AssertEqual(true, (bool)(hasFeedMaterials.Invoke(null, [lowRarityOperationInfo])
                ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: HasFeedMaterials returned nil for an explicit low-rarity feed list.")), "EquipOneKeyFeedRequestHandler behavior explicit equip list bypasses low-rarity cog-only fallback");
            Dictionary<int, int> lowRarityItemDeltas = new();
            NotifyEquipDataList lowRarityNotifyEquipDataList = new();

            int lowRarityFeedExp = (int)(consumeFeedEquips.Invoke(null, [lowRaritySession, lowRarityTargetEquip, lowRarityTargetEquipTable, equipTables, equipBreakThroughTables, lowRarityOperationInfo, lowRarityItemDeltas, lowRarityNotifyEquipDataList])
                ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: ConsumeFeedEquips returned nil for low-rarity explicit feed."));
            AssertEqual(lowRarityExplicitFeedCase.FeedExp, lowRarityFeedExp, "EquipOneKeyFeedRequestHandler behavior low-rarity explicit feed applies full equip exp");
            AssertEqual(false, lowRarityCharacter.Equips.Any(equip => equip.Id == lowRarityFeedEquipId), "EquipOneKeyFeedRequestHandler behavior low-rarity explicit feed consumes listed equip");
            AssertIntegerList([lowRarityFeedEquipId], lowRarityNotifyEquipDataList.DeletedEquipIdList.Select(equipId => (long)equipId).ToArray(), "EquipOneKeyFeedRequestHandler behavior low-rarity explicit feed deleted equip ids");
            AssertEqual(lowRarityFeedExp * -10, lowRarityItemDeltas[AscNet.Common.Database.Inventory.Coin], "EquipOneKeyFeedRequestHandler behavior low-rarity explicit feed coin cost is based on full feed exp");
            AssertEqual(true, lowRarityTargetEquip.Level > targetLevel, "EquipOneKeyFeedRequestHandler behavior low-rarity explicit feed can finish above requested TargetLevel");
            AssertEqual(true, lowRarityTargetEquip.Exp > 0, "EquipOneKeyFeedRequestHandler behavior low-rarity explicit feed carries surplus exp above requested TargetLevel");

            var highRarityFeedCase = FindHighRarityWeaponFeedCase();
            EquipTable highRarityTargetEquipTable = highRarityFeedCase.Target;
            EquipTable normalWeaponFodderEquipTable = highRarityFeedCase.NormalWeaponFeed;
            EquipTable enhancementFodderEquipTable = highRarityFeedCase.EnhancementFeed;
            AssertEqual(false, (bool)(shouldUseCogOnlyEnhancement.Invoke(null, [highRarityTargetEquipTable])
                ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: ShouldUseCogOnlyEnhancement returned nil for a high-rarity target.")), "EquipOneKeyFeedRequestHandler behavior high-rarity weapon uses feed materials");

            const int highRarityTargetEquipId = 7101;
            const int normalWeaponFodderEquipId = 7102;
            const int enhancementFodderEquipId = 7103;
            const int missingFodderEquipId = 7199;
            EquipData highRarityTargetEquip = NewEquip(highRarityTargetEquipId, highRarityTargetEquipTable);
            EquipData normalWeaponFodderEquip = NewEquip(normalWeaponFodderEquipId, normalWeaponFodderEquipTable);
            EquipData enhancementFodderEquip = NewEquip(enhancementFodderEquipId, enhancementFodderEquipTable);
            AscNet.Common.Database.Character highRarityCharacter = NewCharacter(9002, highRarityTargetEquip, normalWeaponFodderEquip, enhancementFodderEquip);
            Session highRaritySession = NewSession(highRarityCharacter, NewInventory(highRarityCharacter.Uid));
            object highRarityOperationInfo = NewOperationInfo([highRarityTargetEquipId, normalWeaponFodderEquipId, enhancementFodderEquipId, normalWeaponFodderEquipId, missingFodderEquipId], null, null);
            Dictionary<int, int> highRarityItemDeltas = new();
            NotifyEquipDataList highRarityNotifyEquipDataList = new();

            int highRarityFeedExp = (int)(consumeFeedEquips.Invoke(null, [highRaritySession, highRarityTargetEquip, highRarityTargetEquipTable, equipTables, equipBreakThroughTables, highRarityOperationInfo, highRarityItemDeltas, highRarityNotifyEquipDataList])
                ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: ConsumeFeedEquips returned nil for high-rarity explicit feed."));
            int expectedHighRarityFeedExp = highRarityFeedCase.NormalWeaponFeedExp + highRarityFeedCase.EnhancementFeedExp;
            AssertEqual(expectedHighRarityFeedExp, highRarityFeedExp, "EquipOneKeyFeedRequestHandler behavior high-rarity weapon consumes normal weapon and Type 99 feed exp");
            AssertEqual(false, highRarityCharacter.Equips.Any(equip => equip.Id == normalWeaponFodderEquipId), "EquipOneKeyFeedRequestHandler behavior high-rarity weapon consumes normal weapon fodder");
            AssertEqual(false, highRarityCharacter.Equips.Any(equip => equip.Id == enhancementFodderEquipId), "EquipOneKeyFeedRequestHandler behavior high-rarity weapon consumes Type 99 enhancement fodder");
            AssertIntegerList([normalWeaponFodderEquipId, enhancementFodderEquipId], highRarityNotifyEquipDataList.DeletedEquipIdList.Select(equipId => (long)equipId).ToArray(), "EquipOneKeyFeedRequestHandler behavior high-rarity exact deleted equip ids");
            AssertEqual(highRarityFeedExp * -10, highRarityItemDeltas[AscNet.Common.Database.Inventory.Coin], "EquipOneKeyFeedRequestHandler behavior high-rarity coin cost is based on full feed exp");

            int itemTargetRequiredExp = FreshRequiredExpToTargetLevel(highRarityTargetEquipTable);
            int itemUseCount = itemTargetRequiredExp / equipExpItemUpgradeInfo.Exp + 1;
            int expectedItemExp = equipExpItemUpgradeInfo.Exp * itemUseCount;
            if (expectedItemExp <= itemTargetRequiredExp)
                throw new InvalidDataException($"EquipOneKeyFeedRequestHandler behavior: expected item exp {expectedItemExp} to exceed target-level gap {itemTargetRequiredExp}.");
            const int itemTargetEquipId = 7201;
            int initialItemCount = itemUseCount + 2;
            int initialCoinCount = equipExpItemUpgradeInfo.Cost * itemUseCount + 5000;
            EquipData itemTargetEquip = NewEquip(itemTargetEquipId, highRarityTargetEquipTable);
            AscNet.Common.Database.Character itemCharacter = NewCharacter(9003, itemTargetEquip);
            AscNet.Common.Database.Inventory itemInventory = NewInventory(
                itemCharacter.Uid,
                new Item { Id = equipExpItem.Id, Count = initialItemCount },
                new Item { Id = AscNet.Common.Database.Inventory.Coin, Count = initialCoinCount });
            Session itemSession = NewSession(itemCharacter, itemInventory);
            object itemOperationInfo = NewOperationInfo(null, [equipExpItem.Id], [itemUseCount]);
            Dictionary<int, int> itemDeltas = new();

            int itemExp = (int)(consumeFeedItems.Invoke(null, [itemSession, itemTables, itemTargetEquipId, itemOperationInfo, itemDeltas])
                ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: ConsumeFeedItems returned nil."));
            AssertEqual(expectedItemExp, itemExp, "EquipOneKeyFeedRequestHandler behavior item feed applies full requested exp instead of capping at TargetLevel");
            AssertEqual(itemUseCount * -1, itemDeltas[equipExpItem.Id], "EquipOneKeyFeedRequestHandler behavior item feed consumes requested material count");
            AssertEqual(equipExpItemUpgradeInfo.Cost * itemUseCount * -1, itemDeltas[AscNet.Common.Database.Inventory.Coin], "EquipOneKeyFeedRequestHandler behavior item feed charges full requested cost");
            AssertEqual(true, itemTargetEquip.Level > targetLevel || itemTargetEquip.Exp > 0, "EquipOneKeyFeedRequestHandler behavior item feed preserves surplus exp beyond requested TargetLevel gap");
            NotifyItemDataList notifyItemDataList = new();
            applyItemDeltas.Invoke(null, [itemSession, itemDeltas, notifyItemDataList]);
            Item notifiedExpItem = notifyItemDataList.ItemDataList.Single(item => item.Id == equipExpItem.Id);
            Item notifiedCoin = notifyItemDataList.ItemDataList.Single(item => item.Id == AscNet.Common.Database.Inventory.Coin);
            AssertEqual(initialItemCount - itemUseCount, notifiedExpItem.Count, "EquipOneKeyFeedRequestHandler behavior NotifyItemDataList consumed requested material count");
            AssertEqual(initialCoinCount - equipExpItemUpgradeInfo.Cost * itemUseCount, notifiedCoin.Count, "EquipOneKeyFeedRequestHandler behavior NotifyItemDataList consumed requested coin cost");

            object response = Activator.CreateInstance(responseType)
                ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: expected response to have a public parameterless constructor.");
            SetRequiredIntegerMember(response, "Code", 0);
            SetRequiredIntegerMember(response, "Breakthrough", highRarityTargetEquip.Breakthrough);
            SetRequiredIntegerMember(response, "Level", highRarityTargetEquip.Level);
            SetRequiredIntegerMember(response, "Exp", (int)highRarityTargetEquip.Exp);
            System.Collections.IList operationInfos = CreateListInstance(MemberValueType(RequiredDataMember(requestType, "OperationInfos")), "EquipOneKeyFeedRequestHandler behavior OperationInfos");
            operationInfos.Add(highRarityOperationInfo);
            SetRequiredIntegerMember(response, "SuccessTimes", operationInfos.Count);
            object roundTripResponse = MessagePackRoundTrip(responseType, response);
            AssertEqual(0, GetRequiredIntegerMember(roundTripResponse, "Code"), "EquipOneKeyFeedRequestHandler behavior response Code");
            AssertEqual(highRarityTargetEquip.Breakthrough, GetRequiredIntegerMember(roundTripResponse, "Breakthrough"), "EquipOneKeyFeedRequestHandler behavior response final Breakthrough");
            AssertEqual(highRarityTargetEquip.Level, GetRequiredIntegerMember(roundTripResponse, "Level"), "EquipOneKeyFeedRequestHandler behavior response final Level");
            AssertEqual((int)highRarityTargetEquip.Exp, GetRequiredIntegerMember(roundTripResponse, "Exp"), "EquipOneKeyFeedRequestHandler behavior response final Exp");
            AssertEqual(operationInfos.Count, GetRequiredIntegerMember(roundTripResponse, "SuccessTimes"), "EquipOneKeyFeedRequestHandler behavior response SuccessTimes");

            object noMaterialOperationInfo = NewOperationInfo(null, null, null);
            AssertEqual(false, (bool)(hasFeedMaterials.Invoke(null, [noMaterialOperationInfo])
                ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: HasFeedMaterials returned nil for a no-material operation.")), "EquipOneKeyFeedRequestHandler behavior no-material operation selects low-rarity cog-only fallback");
            const int cogOnlyTargetEquipId = 7301;
            EquipData cogOnlyTargetEquip = NewEquip(cogOnlyTargetEquipId, lowRarityTargetEquipTable);
            AscNet.Common.Database.Character cogOnlyCharacter = NewCharacter(9004, cogOnlyTargetEquip);
            Dictionary<int, int> cogOnlyItemDeltas = new();
            NotifyEquipDataList cogOnlyNotifyEquipDataList = new();
            int cogOnlyRequiredExp = cogOnlyCharacter.GetEquipExpRequiredToReach(cogOnlyTargetEquipId, targetLevel);
            int cogOnlyAppliedExp = cogOnlyCharacter.AddEquipExpUpTo(cogOnlyTargetEquipId, cogOnlyRequiredExp, targetLevel);
            addItemDelta.Invoke(null, [cogOnlyItemDeltas, AscNet.Common.Database.Inventory.Coin, cogOnlyAppliedExp * -10]);

            AssertEqual(cogOnlyRequiredExp, cogOnlyAppliedExp, "EquipOneKeyFeedRequestHandler behavior cog-only applies only exp required for requested TargetLevel");
            AssertEqual(targetLevel, cogOnlyTargetEquip.Level, "EquipOneKeyFeedRequestHandler behavior no-material cog-only target stops at requested TargetLevel");
            AssertEqual(0, cogOnlyTargetEquip.Exp, "EquipOneKeyFeedRequestHandler behavior no-material cog-only target exp stops exactly at requested TargetLevel");
            AssertEqual(cogOnlyAppliedExp * -10, cogOnlyItemDeltas[AscNet.Common.Database.Inventory.Coin], "EquipOneKeyFeedRequestHandler behavior no-material cog-only coin cost based on capped exp");
            AssertEqual(false, cogOnlyItemDeltas.ContainsKey(equipExpItem.Id), "EquipOneKeyFeedRequestHandler behavior no-material cog-only consumes no material item");
            AssertIntegerList([], cogOnlyNotifyEquipDataList.DeletedEquipIdList.Select(equipId => (long)equipId).ToArray(), "EquipOneKeyFeedRequestHandler behavior no-material cog-only deletes no equip feed material");

            (EquipTable Target, EquipTable Feed, int FeedExp) FindLowRarityWeaponExplicitFeedCase()
            {
                foreach (EquipTable target in equipTables
                    .Where(equip => AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip)
                        && equip.Type == 1
                        && equip.Quality <= 3
                        && equip.Site == 0)
                    .OrderBy(equip => equip.Id))
                {
                    int requiredExp = FreshRequiredExpToTargetLevel(target);
                    if (requiredExp <= 0)
                        continue;

                    foreach (EquipTable feed in equipTables
                        .Where(equip => AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip)
                            && equip.Id != target.Id
                            && equip.Site == 0
                            && equip.Type != 99)
                        .OrderBy(equip => equip.Id))
                    {
                        int feedExp = BaseFeedExp(feed);
                        var finalState = SimulateFinalState(target, feedExp);
                        if (feedExp > requiredExp && finalState.Level > targetLevel && finalState.Exp > 0)
                            return (target, feed, feedExp);
                    }
                }

                throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: expected a low-rarity weapon and explicit equip feed that carries surplus exp beyond TargetLevel.");
            }

            (EquipTable Target, EquipTable NormalWeaponFeed, EquipTable EnhancementFeed, int NormalWeaponFeedExp, int EnhancementFeedExp) FindHighRarityWeaponFeedCase()
            {
                foreach (EquipTable target in equipTables
                    .Where(equip => AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip)
                        && equip.Type == 1
                        && equip.Quality > 3
                        && equip.Site == 0)
                    .OrderBy(equip => equip.Id))
                {
                    int requiredExp = FreshRequiredExpToTargetLevel(target);
                    if (requiredExp <= 0)
                        continue;
                    EquipTable? normalWeaponFeed = equipTables
                        .Where(equip => AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip)
                            && equip.Type == 1
                            && equip.Site == 0
                            && BaseFeedExp(equip) > 0)
                        .OrderBy(equip => equip.Id)
                        .FirstOrDefault();
                    EquipTable? enhancementFeed = equipTables
                        .Where(equip => equip.Type == 99
                            && equip.Site == target.Site
                            && BaseFeedExp(equip) > 0)
                        .OrderBy(equip => equip.Id)
                        .FirstOrDefault();
                    if (normalWeaponFeed is not null && enhancementFeed is not null)
                        return (target, normalWeaponFeed, enhancementFeed, BaseFeedExp(normalWeaponFeed), BaseFeedExp(enhancementFeed));
                }

                throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: expected a high-rarity weapon with normal weapon and Type 99 feed materials.");
            }

            int FreshRequiredExpToTargetLevel(EquipTable target)
            {
                const int probeEquipId = 1;
                EquipData probeEquip = NewEquip(probeEquipId, target);
                AscNet.Common.Database.Character probeCharacter = NewCharacter(9999, probeEquip);
                return probeCharacter.GetEquipExpRequiredToReach(probeEquipId, targetLevel);
            }

            (int Level, int Exp) SimulateFinalState(EquipTable target, int feedExp)
            {
                const int probeEquipId = 2;
                EquipData probeEquip = NewEquip(probeEquipId, target);
                AscNet.Common.Database.Character probeCharacter = NewCharacter(9998, probeEquip);
                probeCharacter.AddEquipExp(probeEquipId, feedExp);
                return (probeEquip.Level, probeEquip.Exp);
            }

            int BaseFeedExp(EquipTable equip)
            {
                return equipBreakThroughTables.FirstOrDefault(breakThrough => breakThrough.EquipId == equip.Id && breakThrough.Times == 0)?.Exp ?? 0;
            }

            EquipData NewEquip(int id, EquipTable table)
            {
                return new EquipData
                {
                    Id = (uint)id,
                    TemplateId = (uint)table.Id,
                    CharacterId = 0,
                    Level = 1,
                    Exp = 0,
                    Breakthrough = 0,
                    ResonanceInfo = [],
                    UnconfirmedResonanceInfo = [],
                    AwakeSlotList = [],
                    IsLock = false,
                    IsRecycle = false
                };
            }

            AscNet.Common.Database.Character NewCharacter(long uid, params EquipData[] equips)
            {
                return new AscNet.Common.Database.Character
                {
                    Uid = uid,
                    Characters = [],
                    Equips = equips.ToList(),
                    Fashions = []
                };
            }

            AscNet.Common.Database.Inventory NewInventory(long uid, params Item[] items)
            {
                return new AscNet.Common.Database.Inventory
                {
                    Uid = uid,
                    Items = items.ToList()
                };
            }

            Session NewSession(AscNet.Common.Database.Character character, AscNet.Common.Database.Inventory inventory)
            {
                Session session = (Session)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Session));
                session.character = character;
                session.inventory = inventory;
                return session;
            }

            object NewOperationInfo(IReadOnlyList<int>? equipIds, IReadOnlyList<int>? itemIds, IReadOnlyList<int>? itemCounts)
            {
                object operationInfo = Activator.CreateInstance(operationInfoType)
                    ?? throw new InvalidDataException("EquipOneKeyFeedRequestHandler behavior: expected operation info to have a public parameterless constructor.");
                if (equipIds is null)
                    SetRequiredMemberValue(operationInfo, "UseEquipIdList", null);
                else
                    SetRequiredIntegerList(operationInfo, "UseEquipIdList", equipIds);
                if (itemIds is null)
                    SetRequiredMemberValue(operationInfo, "UseItemIdList", null);
                else
                    SetRequiredIntegerList(operationInfo, "UseItemIdList", itemIds);
                SetRequiredIntegerMember(operationInfo, "OperationType", 1);
                if (itemCounts is null)
                    SetRequiredMemberValue(operationInfo, "UseItemCountList", null);
                else
                    SetRequiredIntegerList(operationInfo, "UseItemCountList", itemCounts);
                return operationInfo;
            }
        }

        private static object MessagePackRoundTrip(Type type, object value)
        {
            byte[] serialized = MessagePackSerialize(type, value);
            return MessagePackDeserialize(type, serialized)
                ?? throw new InvalidDataException($"{type.FullName} MessagePack round-trip deserialized as nil.");
        }

        private static byte[] MessagePackSerialize(Type type, object value)
        {
            MethodInfo serialize = RequiredMessagePackMethod(
                nameof(MessagePackSerializer.Serialize),
                method => method.ReturnType == typeof(byte[])
                    && method.GetParameters() is { Length: > 0 } parameters
                    && parameters[0].ParameterType.IsGenericParameter);
            object? serialized = InvokeMessagePackGenericMethod(serialize, type, value);
            return serialized as byte[]
                ?? throw new InvalidDataException($"{type.FullName} MessagePack serialize did not return bytes.");
        }

        private static object? MessagePackDeserialize(Type type, byte[] bytes)
        {
            MethodInfo deserialize = RequiredMessagePackMethod(
                nameof(MessagePackSerializer.Deserialize),
                method => method.ReturnType.IsGenericParameter
                    && method.GetParameters() is { Length: > 0 } parameters
                    && (parameters[0].ParameterType == typeof(byte[])
                        || parameters[0].ParameterType == typeof(ReadOnlyMemory<byte>)));
            ParameterInfo firstParameter = deserialize.GetParameters()[0];
            object firstArgument = firstParameter.ParameterType == typeof(ReadOnlyMemory<byte>)
                ? new ReadOnlyMemory<byte>(bytes)
                : bytes;
            return InvokeMessagePackGenericMethod(deserialize, type, firstArgument);
        }

        private static MethodInfo RequiredMessagePackMethod(string name, Func<MethodInfo, bool> predicate)
        {
            MethodInfo[] matches = typeof(MessagePackSerializer)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == name
                    && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 1
                    && predicate(method))
                .ToArray();

            if (matches.Length == 0)
                throw new MissingMethodException(typeof(MessagePackSerializer).FullName, name);

            return matches
                .OrderBy(method => method.GetParameters().Length)
                .ThenBy(method => method.GetParameters()[0].ParameterType == typeof(byte[]) ? 0 : 1)
                .First();
        }

        private static object? InvokeMessagePackGenericMethod(MethodInfo genericMethodDefinition, Type genericArgument, object firstArgument)
        {
            MethodInfo closedMethod = genericMethodDefinition.MakeGenericMethod(genericArgument);
            ParameterInfo[] parameters = closedMethod.GetParameters();
            object?[] arguments = new object?[parameters.Length];
            arguments[0] = firstArgument;
            for (int index = 1; index < parameters.Length; index++)
                arguments[index] = OptionalParameterValue(parameters[index]);

            return closedMethod.Invoke(null, arguments);
        }

        private static object? OptionalParameterValue(ParameterInfo parameter)
        {
            if (parameter.HasDefaultValue && parameter.DefaultValue is not DBNull)
                return parameter.DefaultValue;
            if (parameter.ParameterType == typeof(System.Threading.CancellationToken))
                return default(System.Threading.CancellationToken);
            return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
        }

        private static void SetRequiredIntegerMember(object target, string memberName, int value)
        {
            MemberInfo member = RequiredDataMember(target.GetType(), memberName);
            SetRequiredMemberValue(target, member, ConvertIntegerForType(MemberValueType(member), value));
        }

        private static int GetRequiredIntegerMember(object target, string memberName)
        {
            object? value = GetRequiredMemberValue(target, memberName);
            if (value is null)
                throw new InvalidDataException($"{target.GetType().FullName}.{memberName}: expected an integer, got nil.");
            return Convert.ToInt32(value);
        }

        private static void SetRequiredIntegerList(object target, string memberName, IReadOnlyList<int> values)
        {
            MemberInfo member = RequiredDataMember(target.GetType(), memberName);
            object list = CreateIntegerList(MemberValueType(member), values, $"{target.GetType().FullName}.{memberName}");
            SetRequiredMemberValue(target, member, list);
        }

        private static IReadOnlyList<long> GetRequiredIntegerList(object target, string memberName)
        {
            object? value = GetRequiredMemberValue(target, memberName);
            if (value is null)
                throw new InvalidDataException($"{target.GetType().FullName}.{memberName}: expected a list, got nil.");
            return ReadIntegerList(value, $"{target.GetType().FullName}.{memberName}");
        }

        private static void AssertIntegerList(IReadOnlyList<long> expected, IReadOnlyList<long> actual, string name)
        {
            AssertEqual(expected.Count, actual.Count, $"{name} count");
            for (int index = 0; index < expected.Count; index++)
                AssertEqual(expected[index], actual[index], $"{name}[{index}]");
        }

        private static void AssertIntegerDictionary(IReadOnlyDictionary<int, int> expected, IReadOnlyDictionary<int, int> actual, string name)
        {
            AssertEqual(expected.Count, actual.Count, $"{name} count");
            foreach (KeyValuePair<int, int> entry in expected.OrderBy(entry => entry.Key))
            {
                if (!actual.TryGetValue(entry.Key, out int actualValue))
                    throw new InvalidDataException($"{name}: missing key {entry.Key}.");
                AssertEqual(entry.Value, actualValue, $"{name}[{entry.Key}]");
            }
        }

        private static void AssertStringDictionary(IReadOnlyDictionary<int, string> expected, IReadOnlyDictionary<int, string> actual, string name)
        {
            AssertEqual(expected.Count, actual.Count, $"{name} count");
            foreach (KeyValuePair<int, string> entry in expected.OrderBy(entry => entry.Key))
            {
                if (!actual.TryGetValue(entry.Key, out string? actualValue))
                    throw new InvalidDataException($"{name}: missing key {entry.Key}.");
                AssertEqual(entry.Value, actualValue, $"{name}[{entry.Key}]");
            }
        }

        private static void AssertRetailDrawInfoArt(
            DrawInfo drawInfo,
            int expectedId,
            int expectedGroupId,
            string expectedBanner,
            IReadOnlyDictionary<int, string> expectedResources,
            IReadOnlyDictionary<int, int> expectedResourceIds,
            IReadOnlyList<long> expectedPurchaseUiType,
            int expectedGroupSubType,
            string name)
        {
            AssertEqual(expectedId, drawInfo.Id, $"{name} Id");
            AssertEqual(expectedGroupId, drawInfo.GroupId, $"{name} GroupId");
            AssertEqual(expectedBanner, drawInfo.Banner, $"{name} Banner");
            AssertStringDictionary(expectedResources, drawInfo.Resources, $"{name} Resources");
            AssertIntegerDictionary(expectedResourceIds, drawInfo.ResourceIds, $"{name} ResourceIds");
            AssertIntegerList([1, 10], drawInfo.BtnDrawCount.Select(count => (long)count).ToArray(), $"{name} BtnDrawCount");
            AssertIntegerList(expectedPurchaseUiType, drawInfo.PurchaseUiType.Select(uiType => (long)uiType).ToArray(), $"{name} PurchaseUiType");
            AssertEqual(0, drawInfo.UpGoodsId, $"{name} UpGoodsId");
            AssertEqual(expectedGroupSubType, drawInfo.GroupSubType, $"{name} GroupSubType");
        }

        private static DrawGetDrawInfoListResponse ReadDrawInfoListForGroup(int groupId, int packetId, long playerId, string harnessName)
        {
            DrawGetDrawInfoListRequest request = new()
            {
                GroupId = groupId
            };

            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                harnessName);
            InvokeRegisteredRequestHandler("DrawGetDrawInfoListRequest", harness.Session, packetId, request);
            return ReadResponsePayload<DrawGetDrawInfoListResponse>(
                harness,
                packetId,
                nameof(DrawGetDrawInfoListResponse),
                $"{harnessName} response");
        }

        private static void AssertRequiredMemberNull(object target, string memberName, string name)
        {
            if (GetRequiredMemberValue(target, memberName) is not null)
                throw new InvalidDataException($"{name}: expected nil.");
        }

        private static object CreateIntegerList(Type listType, IReadOnlyList<int> values, string name)
        {
            Type elementType = RequiredIntegerListElementType(listType, name);
            System.Collections.IList list = CreateListInstance(listType, name);
            foreach (int value in values)
                list.Add(ConvertIntegerForType(elementType, value));
            return list;
        }

        private static IReadOnlyList<long> ReadIntegerList(object value, string name)
        {
            if (value is not System.Collections.IEnumerable values || value is string)
                throw new InvalidDataException($"{name}: expected a list.");

            List<long> result = new();
            foreach (object? item in values)
            {
                if (item is null)
                    throw new InvalidDataException($"{name}: expected integer entries, got nil.");
                result.Add(Convert.ToInt64(item));
            }

            return result;
        }

        private static System.Collections.IList CreateListInstance(Type listType, string name)
        {
            Type elementType = RequiredListElementType(listType, name);
            Type concreteListType = typeof(List<>).MakeGenericType(elementType);
            if (!listType.IsAssignableFrom(concreteListType))
                throw new InvalidDataException($"{name}: expected a list type assignable from {concreteListType.FullName}, got {listType.FullName}.");

            return (System.Collections.IList)(Activator.CreateInstance(concreteListType)
                ?? throw new InvalidDataException($"{name}: expected to construct {concreteListType.FullName}."));
        }

        private static Type RequiredIntegerListElementType(Type listType, string name)
        {
            Type elementType = RequiredListElementType(listType, name);
            if (elementType != typeof(int)
                && elementType != typeof(uint)
                && elementType != typeof(long)
                && elementType != typeof(ulong))
                throw new InvalidDataException($"{name}: expected an integer list, got {listType.FullName}.");
            return elementType;
        }

        private static Type RequiredListElementType(Type listType, string name)
        {
            if (listType.IsGenericType && listType.GetGenericArguments().Length == 1)
                return listType.GetGenericArguments()[0];

            Type? enumerableInterface = listType.GetInterfaces()
                .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableInterface is not null)
                return enumerableInterface.GetGenericArguments()[0];

            throw new InvalidDataException($"{name}: expected a generic list type, got {listType.FullName}.");
        }

        private static object ConvertIntegerForType(Type targetType, int value)
        {
            if (targetType == typeof(int))
                return value;
            if (targetType == typeof(uint))
                return (uint)value;
            if (targetType == typeof(long))
                return (long)value;
            if (targetType == typeof(ulong))
                return (ulong)value;

            throw new InvalidDataException($"Expected integer field or list element type, got {targetType.FullName}.");
        }

        private static MemberInfo RequiredDataMember(Type type, string memberName)
        {
            MemberInfo[] members = type.GetMember(memberName, BindingFlags.Instance | BindingFlags.Public)
                .Where(member => member is FieldInfo || member is PropertyInfo { GetMethod: not null })
                .ToArray();

            return members.Length switch
            {
                1 => members[0],
                0 => throw new MissingMemberException(type.FullName, memberName),
                _ => throw new AmbiguousMatchException($"{type.FullName}.{memberName} matched {members.Length} members.")
            };
        }

        private static Type MemberValueType(MemberInfo member)
        {
            return member switch
            {
                FieldInfo field => field.FieldType,
                PropertyInfo property => property.PropertyType,
                _ => throw new InvalidDataException($"{member.DeclaringType?.FullName}.{member.Name}: expected a field or property.")
            };
        }

        private static object? GetRequiredMemberValue(object target, string memberName)
        {
            return GetRequiredMemberValue(target, RequiredDataMember(target.GetType(), memberName));
        }

        private static object? GetRequiredMemberValue(object target, MemberInfo member)
        {
            return member switch
            {
                FieldInfo field => field.GetValue(target),
                PropertyInfo property => property.GetValue(target),
                _ => throw new InvalidDataException($"{member.DeclaringType?.FullName}.{member.Name}: expected a field or property.")
            };
        }

        private static void SetRequiredMemberValue(object target, string memberName, object? value)
        {
            SetRequiredMemberValue(target, RequiredDataMember(target.GetType(), memberName), value);
        }

        private static void SetRequiredMemberValue(object target, MemberInfo member, object? value)
        {
            switch (member)
            {
                case FieldInfo field:
                    field.SetValue(target, value);
                    break;
                case PropertyInfo { SetMethod: not null } property:
                    property.SetValue(target, value);
                    break;
                case PropertyInfo property:
                    throw new MissingMethodException(property.DeclaringType?.FullName, $"set_{property.Name}");
                default:
                    throw new InvalidDataException($"{member.DeclaringType?.FullName}.{member.Name}: expected a field or property.");
            }
        }


        private static void AssertNotifyEquipDataListHasNoPhantomCleanupPayload(string name)
        {
            _ = RequiredIntegerListElementType(MemberValueType(RequiredDataMember(typeof(NotifyEquipDataList), "DeletedEquipIdList")), $"{name} DeletedEquipIdList");

            Type? autoRecycleNotify = typeof(NotifyEquipDataList).Assembly.GetType("AscNet.Common.MsgPack.NotifyEquipAutoRecycleChipList", throwOnError: false);
            if (autoRecycleNotify is not null)
                throw new InvalidDataException($"{name}: expected NotifyEquipAutoRecycleChipList to be absent; equip sync must not depend on an auto-recycle phantom cleanup push.");
        }


        private static void AssertEquipCommandSyncContract(
            Type equipCommandType,
            MethodInfo execute,
            MethodInfo syncHelper,
            MethodInfo sendLoginState,
            FieldInfo commandSession,
            FieldInfo sessionPlayer,
            FieldInfo sessionCharacter,
            MethodInfo playerDataGetter,
            MethodInfo playerDataIdGetter,
            MethodInfo characterFromUid,
            string name)
        {
            AssertEquipCommandAcceptsSyncWithoutTarget(equipCommandType, $"{name} metadata");
            AssertEquipCommandExecuteSyncCallsHelper(execute, syncHelper, $"{name} Execute");
            AssertEquipSyncReloadsCharacterAndResendsLoginState(
                syncHelper,
                sendLoginState,
                commandSession,
                sessionPlayer,
                sessionCharacter,
                playerDataGetter,
                playerDataIdGetter,
                characterFromUid,
                $"{name} reload");
        }

        private static void AssertEquipCommandAcceptsSyncWithoutTarget(Type equipCommandType, string name)
        {
            object? command;
            try
            {
                command = Activator.CreateInstance(
                    equipCommandType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: [null, new[] { "sync" }, true],
                    culture: null);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException)
            {
                throw new InvalidDataException($"{name}: expected 'equip sync' with no target argument to pass command validation.", exception.InnerException);
            }

            if (command is null)
                throw new InvalidDataException($"{name}: expected to construct EquipCommand for 'equip sync'.");
        }

        private static void AssertEquipCommandExecuteSyncCallsHelper(MethodInfo execute, MethodInfo syncHelper, string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(execute).ToList();
            int syncHelperIndex = FindCallIndex(instructions, syncHelper, startIndex: 0);
            if (syncHelperIndex < 0)
                throw new InvalidDataException($"{name}: expected Execute to call SyncEquipsFromDatabase for the sync operation.");

            if (!HasConditionalBranchGuard(instructions, syncHelperIndex, syncHelperIndex))
                throw new InvalidDataException($"{name}: expected sync reload to be guarded by the sync operation branch.");

            int returnIndex = instructions.FindIndex(syncHelperIndex + 1, instruction => instruction.OpCode.FlowControl == FlowControl.Return);
            if (returnIndex < 0)
                throw new InvalidDataException($"{name}: expected sync path to return immediately after reload.");
        }

        private static void AssertEquipSyncReloadsCharacterAndResendsLoginState(
            MethodInfo method,
            MethodInfo sendLoginState,
            FieldInfo commandSession,
            FieldInfo sessionPlayer,
            FieldInfo sessionCharacter,
            MethodInfo playerDataGetter,
            MethodInfo playerDataIdGetter,
            MethodInfo characterFromUid,
            string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();
            int characterFromUidIndex = FindCallIndex(instructions, characterFromUid, startIndex: 0);
            if (characterFromUidIndex < 0)
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to reload Character.FromUid.");
            AssertCharacterFromUidUsesSessionPlayerUid(instructions, characterFromUidIndex, commandSession, sessionPlayer, playerDataGetter, playerDataIdGetter, name);

            int sessionCharacterAssignmentIndex = FindFieldAssignmentIndex(instructions, sessionCharacter, characterFromUidIndex + 1, instructions.Count - 1);
            if (sessionCharacterAssignmentIndex < 0)
                throw new InvalidDataException($"{name}: expected session.character to be replaced with the reloaded character before login-state resend.");

            int sendLoginStateIndex = FindCallIndex(instructions, sendLoginState, startIndex: sessionCharacterAssignmentIndex + 1);
            if (sendLoginStateIndex < 0)
                throw new InvalidDataException($"{name}: expected sync to resend NotifyLogin state so stock client code reinitializes equipment.");

            MethodInfo sendPush = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendPush),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 1);
            int directPushIndex = FindGenericCallIndex(instructions, sendPush, startIndex: 0);
            if (directPushIndex >= 0)
                throw new InvalidDataException($"{name}: expected sync to refresh only through AccountModule.SendLoginState and not call Session.SendPush directly.");
        }

        private static void AssertCharacterFromUidUsesSessionPlayerUid(
            List<IlInstruction> instructions,
            int characterFromUidIndex,
            FieldInfo commandSession,
            FieldInfo sessionPlayer,
            MethodInfo playerDataGetter,
            MethodInfo playerDataIdGetter,
            string name)
        {
            bool loadedCommandSession = false;
            bool loadedSessionPlayer = false;
            bool loadedPlayerData = false;
            bool loadedPlayerDataId = false;
            int firstCandidate = Math.Max(0, characterFromUidIndex - 12);
            for (int index = firstCandidate; index < characterFromUidIndex; index++)
            {
                if (instructions[index].Operand is FieldInfo loadedField)
                {
                    loadedCommandSession |= FieldsMatch(loadedField, commandSession);
                    loadedSessionPlayer |= FieldsMatch(loadedField, sessionPlayer);
                    continue;
                }

                if (instructions[index].Operand is MethodBase calledMethod)
                {
                    loadedPlayerData |= MethodsMatch(calledMethod, playerDataGetter);
                    loadedPlayerDataId |= MethodsMatch(calledMethod, playerDataIdGetter);
                }
            }

            if (!loadedCommandSession || !loadedSessionPlayer || !loadedPlayerData || !loadedPlayerDataId)
                throw new InvalidDataException($"{name}: expected Character.FromUid to be called with session.player.PlayerData.Id.");
        }


        private static void AssertRecentSessionCharacterEquipsSource(List<IlInstruction> instructions, int consumerIndex, FieldInfo sessionCharacter, MethodInfo characterEquipsGetter, string name)
        {
            int equipsGetterIndex = -1;
            int firstGetterCandidate = Math.Max(0, consumerIndex - 24);
            for (int index = consumerIndex - 1; index >= firstGetterCandidate; index--)
            {
                if (instructions[index].Operand is MethodBase calledMethod && MethodsMatch(calledMethod, characterEquipsGetter))
                {
                    equipsGetterIndex = index;
                    break;
                }
            }

            if (equipsGetterIndex < 0)
                throw new InvalidDataException($"{name}: expected the consumed equip list to come from Character.Equips.");

            int firstCharacterCandidate = Math.Max(0, equipsGetterIndex - 8);
            for (int index = equipsGetterIndex - 1; index >= firstCharacterCandidate; index--)
            {
                if (instructions[index].Operand is FieldInfo loadedField && FieldsMatch(loadedField, sessionCharacter))
                    return;
            }

            throw new InvalidDataException($"{name}: expected Character.Equips to be loaded from Session.character.");
        }

        private static void AssertLevelUpMaxCapResponsePrecedesInventoryMutation(MethodInfo method, MethodInfo inventoryDo, MethodInfo inventorySave, string name)
        {
            FieldInfo responseCode = typeof(CharacterLevelUpResponse).GetField(nameof(CharacterLevelUpResponse.Code), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(CharacterLevelUpResponse).FullName, nameof(CharacterLevelUpResponse.Code));
            MethodInfo sendResponse = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendResponse),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 2);

            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();
            int maxLevelCodeIndex = FindFieldAssignmentIndex(instructions, responseCode, expectedValue: 20009014);
            if (maxLevelCodeIndex < 0)
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to assign CharacterLevelUpResponse.Code = 20009014.");

            int maxLevelResponseIndex = FindGenericCallIndex(instructions, sendResponse, typeof(CharacterLevelUpResponse), startIndex: maxLevelCodeIndex + 1);
            if (maxLevelResponseIndex < 0)
                throw new InvalidDataException($"{name}: expected CharacterLevelUpResponse.Code = 20009014 to feed Session.SendResponse<CharacterLevelUpResponse>.");

            if (!HasConditionalBranchGuard(instructions, maxLevelCodeIndex, maxLevelResponseIndex))
                throw new InvalidDataException($"{name}: expected CharacterLevelUpResponse.Code = 20009014 to be reached through a conditional commandant-cap branch.");

            int firstInventoryMutationIndex = FindFirstInventoryMutationIndex(instructions, inventoryDo, inventorySave);
            if (firstInventoryMutationIndex < 0)
                throw new InvalidDataException($"{name}: expected the success path to consume inventory or save inventory changes.");

            if (firstInventoryMutationIndex <= maxLevelResponseIndex)
                throw new InvalidDataException($"{name}: expected the max-level response to be sent before inventory consumption/save.");

            if (!PathExits(instructions, maxLevelResponseIndex + 1))
                throw new InvalidDataException($"{name}: expected the max-level response path to exit before reaching inventory consumption/save.");
        }

        private static void AssertRequestFieldFeedsSetterBeforePersistence(MethodInfo method, FieldInfo sourceField, MethodInfo targetSetter, MethodInfo persistenceMethod, string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();
            int setterIndex = instructions.FindIndex(instruction => instruction.Operand is MethodBase calledMethod && MethodsMatch(calledMethod, targetSetter));
            if (setterIndex < 0)
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to assign through {targetSetter.DeclaringType?.FullName}.{targetSetter.Name}.");

            bool loadsSourceField = false;
            for (int previousIndex = setterIndex - 1; previousIndex >= 0 && previousIndex >= setterIndex - 8; previousIndex--)
            {
                if (instructions[previousIndex].Operand is FieldInfo loadedField && FieldsMatch(loadedField, sourceField))
                {
                    loadsSourceField = true;
                    break;
                }
            }

            if (!loadsSourceField)
                throw new InvalidDataException($"{name}: expected assignment through {targetSetter.DeclaringType?.FullName}.{targetSetter.Name} to use {sourceField.DeclaringType?.FullName}.{sourceField.Name}.");

            int persistenceIndex = instructions.FindIndex(setterIndex + 1, instruction => instruction.Operand is MethodBase calledMethod && MethodsMatch(calledMethod, persistenceMethod));
            if (persistenceIndex < 0)
                throw new InvalidDataException($"{name}: expected {persistenceMethod.DeclaringType?.FullName}.{persistenceMethod.Name} after assigning the selected value.");
        }

        private static void AssertHandlerSendsResponseCode(MethodInfo method, FieldInfo responseCodeField, int expectedCode, string name)
        {
            MethodInfo sendResponse = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendResponse),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 2);
            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();

            int codeIndex = FindFieldAssignmentIndex(instructions, responseCodeField, expectedCode);
            if (codeIndex < 0)
                throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} to assign {responseCodeField.DeclaringType?.FullName}.{responseCodeField.Name} = {expectedCode}.");

            Type responseType = responseCodeField.DeclaringType
                ?? throw new InvalidDataException($"{name}: response code field has no declaring type.");
            int responseIndex = FindGenericCallIndex(instructions, sendResponse, responseType, codeIndex + 1);
            if (responseIndex < 0)
                throw new InvalidDataException($"{name}: expected response code {expectedCode} to feed Session.SendResponse<{responseType.FullName}>.");

            if (!HasConditionalBranchGuard(instructions, codeIndex, responseIndex))
                throw new InvalidDataException($"{name}: expected response code {expectedCode} to be reached through a conditional validation branch.");

            if (!PathExits(instructions, responseIndex + 1))
                throw new InvalidDataException($"{name}: expected response code {expectedCode} path to exit before the success path.");
        }

        private static void AssertGenderValidationRejectsOnlyOutsideCurrentClientRange(MethodInfo method, FieldInfo requestGenderField, FieldInfo responseCodeField, string name)
        {
            MethodInfo sendResponse = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendResponse),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 2);
            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();

            int invalidCodeIndex = FindFieldAssignmentIndex(instructions, responseCodeField, expectedValue: 20002020);
            if (invalidCodeIndex < 0)
                throw new InvalidDataException($"{name}: expected invalid gender response code 20002020.");

            Type responseType = responseCodeField.DeclaringType
                ?? throw new InvalidDataException($"{name}: response code field has no declaring type.");
            int invalidResponseIndex = FindGenericCallIndex(instructions, sendResponse, responseType, invalidCodeIndex + 1);
            if (invalidResponseIndex < 0)
                throw new InvalidDataException($"{name}: expected invalid gender response code 20002020 to feed Session.SendResponse<{responseType.FullName}>.");

            bool hasNormalizedRangeGuard = HasNormalizedInclusiveRangeGuard(
                instructions,
                requestGenderField,
                minimum: 1,
                maximum: 3,
                invalidCodeIndex,
                invalidResponseIndex);
            bool hasLowerBoundGuard = HasGenderBoundBranch(instructions, requestGenderField, bound: 1, invalidCodeIndex, invalidResponseIndex, lowerBound: true);
            bool hasUpperBoundGuard = HasGenderBoundBranch(instructions, requestGenderField, bound: 3, invalidCodeIndex, invalidResponseIndex, lowerBound: false);

            if (!hasNormalizedRangeGuard && !hasLowerBoundGuard)
                throw new InvalidDataException($"{name}: expected gender 0 to reach invalid response 20002020 while gender 1 continues.");

            if (!hasNormalizedRangeGuard && !hasUpperBoundGuard)
                throw new InvalidDataException($"{name}: expected gender 4 to reach invalid response 20002020 while current-client gender 3 continues.");

            if (!PathExits(instructions, invalidResponseIndex + 1))
                throw new InvalidDataException($"{name}: expected invalid gender response path to exit before success handling.");
        }

        private static void AssertSameGenderResponseRequiresAlreadySetGender(MethodInfo method, FieldInfo requestGenderField, MethodInfo playerGenderGetter, MethodInfo playerChangeGenderTimeGetter, FieldInfo responseCodeField, string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();

            int sameGenderCodeIndex = FindFieldAssignmentIndex(instructions, responseCodeField, expectedValue: 20002021);
            if (sameGenderCodeIndex < 0)
                throw new InvalidDataException($"{name}: expected unchanged gender response code 20002021.");

            if (!HasMethodCallComparedToConstantBefore(instructions, playerGenderGetter, expectedValue: 0, endIndex: sameGenderCodeIndex))
                throw new InvalidDataException($"{name}: expected same-gender rejection to be gated by current PlayerData.Gender being already set (> 0).");

            if (!HasMethodCallStrictlyPositiveComparisonBefore(instructions, playerChangeGenderTimeGetter, endIndex: sameGenderCodeIndex))
                throw new InvalidDataException($"{name}: expected same-gender rejection to require PlayerData.ChangeGenderTime > 0 before returning 20002021.");

            if (!HasSameGenderComparisonBefore(instructions, playerGenderGetter, requestGenderField, sameGenderCodeIndex))
                throw new InvalidDataException($"{name}: expected response code 20002021 to be guarded by comparing current PlayerData.Gender with ChangePlayerGenderRequest.Gender.");
        }

        private static void AssertFirstGenderSetupRewardPath(
            MethodInfo method,
            MethodInfo changeGenderTimeGetter,
            MethodInfo changeGenderTimeSetter,
            FieldInfo responseRewardGoodsListField,
            MethodInfo inventoryDo,
            MethodInfo inventorySave,
            MethodInfo playerSave,
            string name)
        {
            MethodInfo sendPush = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendPush),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 1);
            MethodInfo sendResponse = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendResponse),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 2);
            MethodInfo rewardGoodsListAdd = RequiredMethod(
                typeof(List<RewardGoods>),
                nameof(List<RewardGoods>.Add),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(RewardGoods)]);
            MethodInfo rewardGoodsRewardTypeSetter = RequiredMethod(
                typeof(RewardGoods),
                $"set_{nameof(RewardGoods.RewardType)}",
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int)]);
            MethodInfo rewardGoodsTemplateIdSetter = RequiredMethod(
                typeof(RewardGoods),
                $"set_{nameof(RewardGoods.TemplateId)}",
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int)]);
            MethodInfo rewardGoodsCountSetter = RequiredMethod(
                typeof(RewardGoods),
                $"set_{nameof(RewardGoods.Count)}",
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int)]);

            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();

            int changeGenderTimeIndex = FindCallIndex(instructions, changeGenderTimeSetter, startIndex: 0);
            if (changeGenderTimeIndex < 0)
                throw new InvalidDataException($"{name}: expected success path to set PlayerData.ChangeGenderTime.");

            int rewardInventoryIndex = FindMethodCallWithRecentConstants(instructions, inventoryDo, AscNet.Common.Database.Inventory.FreeGem, 50);
            if (rewardInventoryIndex < 0)
                throw new InvalidDataException($"{name}: expected first setup path to grant 50 Black Cards through Inventory.Do(Inventory.FreeGem, 50).");

            if (!HasConditionalBranchGuard(instructions, rewardInventoryIndex, rewardInventoryIndex))
                throw new InvalidDataException($"{name}: expected Black Card reward grant to be guarded by first gender setup.");

            if (!HasMethodCallStrictlyPositiveComparisonBefore(instructions, changeGenderTimeGetter, endIndex: rewardInventoryIndex))
                throw new InvalidDataException($"{name}: expected first setup reward guard to classify PlayerData.ChangeGenderTime <= 0 as incomplete setup.");

            int notifyItemPushIndex = FindGenericCallIndex(instructions, sendPush, typeof(NotifyItemDataList), rewardInventoryIndex + 1);
            if (notifyItemPushIndex < 0)
                throw new InvalidDataException($"{name}: expected first setup reward to push NotifyItemDataList.");

            int rewardGoodsListLoadIndex = FindFieldLoadIndex(instructions, responseRewardGoodsListField, notifyItemPushIndex + 1);
            if (rewardGoodsListLoadIndex < 0)
                throw new InvalidDataException($"{name}: expected first setup response to load ChangePlayerGenderResponse.RewardGoodsList.");

            int rewardGoodsAddIndex = FindCallIndex(instructions, rewardGoodsListAdd, rewardGoodsListLoadIndex + 1);
            if (rewardGoodsAddIndex < 0)
                throw new InvalidDataException($"{name}: expected first setup response to add a RewardGoods entry.");

            AssertSetterAssignedConstantBetween(instructions, rewardGoodsRewardTypeSetter, (int)RewardType.Item, rewardGoodsListLoadIndex, rewardGoodsAddIndex, $"{name} reward type");
            AssertSetterAssignedConstantBetween(instructions, rewardGoodsTemplateIdSetter, AscNet.Common.Database.Inventory.FreeGem, rewardGoodsListLoadIndex, rewardGoodsAddIndex, $"{name} reward item");
            AssertSetterAssignedConstantBetween(instructions, rewardGoodsCountSetter, 50, rewardGoodsListLoadIndex, rewardGoodsAddIndex, $"{name} reward count");

            int inventorySaveIndex = FindCallIndex(instructions, inventorySave, rewardGoodsAddIndex + 1);
            if (inventorySaveIndex < 0)
                throw new InvalidDataException($"{name}: expected first setup reward to persist Inventory.Save.");

            int playerSaveIndex = FindCallIndex(instructions, playerSave, inventorySaveIndex + 1);
            if (playerSaveIndex < 0)
                throw new InvalidDataException($"{name}: expected success path to save Player after first setup reward handling.");

            int finalResponseIndex = FindGenericCallIndex(instructions, sendResponse, typeof(ChangePlayerGenderResponse), playerSaveIndex + 1);
            if (finalResponseIndex < 0)
                throw new InvalidDataException($"{name}: expected saved success path to send ChangePlayerGenderResponse.");

            if (changeGenderTimeIndex >= playerSaveIndex)
                throw new InvalidDataException($"{name}: expected ChangeGenderTime to be set before Player.Save.");
            if (notifyItemPushIndex >= inventorySaveIndex)
                throw new InvalidDataException($"{name}: expected NotifyItemDataList push before Inventory.Save.");
            if (inventorySaveIndex >= finalResponseIndex)
                throw new InvalidDataException($"{name}: expected Inventory.Save before the success response.");
            if (playerSaveIndex >= finalResponseIndex)
                throw new InvalidDataException($"{name}: expected Player.Save before the success response.");
        }

        private static void AssertLiveGenderRefreshBeforeSuccessResponse(
            MethodInfo method,
            MethodInfo playerDataGetter,
            MethodInfo playerGenderGetter,
            MethodInfo playerGenderSetter,
            MethodInfo changeGenderTimeGetter,
            MethodInfo changeGenderTimeSetter,
            FieldInfo responseGenderField,
            FieldInfo responseChangeGenderTimeField,
            FieldInfo responseNextCanChangeTimeField,
            FieldInfo responsePlayerDataField,
            FieldInfo notifyGenderField,
            FieldInfo notifyChangeGenderTimeField,
            string name)
        {
            MethodInfo sendPush = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendPush),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 1);
            MethodInfo sendResponse = RequiredGenericMethodDefinition(
                typeof(Session),
                nameof(Session.SendResponse),
                BindingFlags.Instance | BindingFlags.Public,
                parameterCount: 2);

            List<IlInstruction> instructions = ReadIlInstructions(method).ToList();

            int genderSetIndex = FindCallIndex(instructions, playerGenderSetter, startIndex: 0);
            if (genderSetIndex < 0)
                throw new InvalidDataException($"{name}: expected success path to set PlayerData.Gender.");

            int changeGenderTimeSetIndex = FindCallIndex(instructions, changeGenderTimeSetter, startIndex: genderSetIndex + 1);
            if (changeGenderTimeSetIndex < 0)
                throw new InvalidDataException($"{name}: expected success path to set PlayerData.ChangeGenderTime after PlayerData.Gender.");

            int updatedStateIndex = Math.Max(genderSetIndex, changeGenderTimeSetIndex);
            int notifyPushIndex = FindGenericCallIndex(instructions, sendPush, typeof(NotifyPlayerGender), updatedStateIndex + 1);
            if (notifyPushIndex < 0)
                throw new InvalidDataException($"{name}: expected updated gender state to be pushed through NotifyPlayerGender.");

            int successResponseIndex = FindGenericCallIndex(instructions, sendResponse, typeof(ChangePlayerGenderResponse), notifyPushIndex + 1);
            if (successResponseIndex < 0)
                throw new InvalidDataException($"{name}: expected NotifyPlayerGender to precede the success ChangePlayerGenderResponse.");

            int notifyGenderSetIndex = FindFieldAssignmentIndex(instructions, notifyGenderField, updatedStateIndex + 1, notifyPushIndex);
            if (notifyGenderSetIndex < 0)
                throw new InvalidDataException($"{name}: expected NotifyPlayerGender.Gender to be populated before SendPush.");
            AssertFieldAssignmentUsesRecentCall(instructions, notifyGenderSetIndex, playerGenderGetter, $"{name} notify gender source");

            int notifyChangeGenderTimeSetIndex = FindFieldAssignmentIndex(instructions, notifyChangeGenderTimeField, updatedStateIndex + 1, notifyPushIndex);
            if (notifyChangeGenderTimeSetIndex < 0)
                throw new InvalidDataException($"{name}: expected NotifyPlayerGender.ChangeGenderTime to be populated before SendPush.");
            AssertFieldAssignmentUsesRecentCall(instructions, notifyChangeGenderTimeSetIndex, changeGenderTimeGetter, $"{name} notify change-time source");

            int responseGenderSetIndex = FindFieldAssignmentIndex(instructions, responseGenderField, updatedStateIndex + 1, successResponseIndex);
            if (responseGenderSetIndex < 0)
                throw new InvalidDataException($"{name}: expected ChangePlayerGenderResponse.Gender to be populated before SendResponse.");
            AssertFieldAssignmentUsesRecentCall(instructions, responseGenderSetIndex, playerGenderGetter, $"{name} response gender source");

            int responseChangeGenderTimeSetIndex = FindFieldAssignmentIndex(instructions, responseChangeGenderTimeField, updatedStateIndex + 1, successResponseIndex);
            if (responseChangeGenderTimeSetIndex < 0)
                throw new InvalidDataException($"{name}: expected ChangePlayerGenderResponse.ChangeGenderTime to be populated before SendResponse.");
            AssertFieldAssignmentUsesRecentCall(instructions, responseChangeGenderTimeSetIndex, changeGenderTimeGetter, $"{name} response change-time source");

            int responseNextCanChangeTimeSetIndex = FindFieldAssignmentIndex(instructions, responseNextCanChangeTimeField, updatedStateIndex + 1, successResponseIndex);
            if (responseNextCanChangeTimeSetIndex < 0)
                throw new InvalidDataException($"{name}: expected ChangePlayerGenderResponse.NextCanChangeTime to be populated before SendResponse.");
            AssertFieldAssignmentUsesRecentCall(instructions, responseNextCanChangeTimeSetIndex, changeGenderTimeGetter, $"{name} response next-change-time source");

            int responsePlayerDataSetIndex = FindFieldAssignmentIndex(instructions, responsePlayerDataField, updatedStateIndex + 1, successResponseIndex);
            if (responsePlayerDataSetIndex < 0)
                throw new InvalidDataException($"{name}: expected ChangePlayerGenderResponse.PlayerData to be populated before SendResponse.");
            AssertFieldAssignmentUsesRecentCall(instructions, responsePlayerDataSetIndex, playerDataGetter, $"{name} response player-data source");

            if (notifyPushIndex >= successResponseIndex)
                throw new InvalidDataException($"{name}: expected NotifyPlayerGender push before the success response.");
        }

        private static bool HasGenderBoundBranch(List<IlInstruction> instructions, FieldInfo requestGenderField, int bound, int invalidCodeIndex, int invalidResponseIndex, bool lowerBound)
        {
            int firstCandidateIndex = Math.Max(0, invalidCodeIndex - 48);
            int invalidResponseOffset = instructions[invalidResponseIndex].Offset;

            for (int index = firstCandidateIndex; index < invalidCodeIndex; index++)
            {
                IlInstruction instruction = instructions[index];
                if (instruction.OpCode.FlowControl != FlowControl.Cond_Branch || instruction.Operand is not int targetOffset)
                    continue;
                if (!InstructionWindowLoadsFieldAndConstant(instructions, requestGenderField, bound, index, maxInstructionsBack: 8))
                    continue;

                bool targetReachesInvalidResponse = targetOffset > instruction.Offset && targetOffset <= invalidResponseOffset;
                bool targetSkipsInvalidResponse = targetOffset > invalidResponseOffset;

                if (lowerBound)
                {
                    if ((targetReachesInvalidResponse && IsLessThanBranch(instruction.OpCode))
                        || (targetSkipsInvalidResponse && IsGreaterThanOrEqualBranch(instruction.OpCode)))
                        return true;
                }
                else
                {
                    if ((targetReachesInvalidResponse && IsGreaterThanBranch(instruction.OpCode))
                        || (targetSkipsInvalidResponse && IsLessThanOrEqualBranch(instruction.OpCode)))
                        return true;
                }
            }

            return false;
        }

        private static bool HasNormalizedInclusiveRangeGuard(List<IlInstruction> instructions, FieldInfo requestGenderField, int minimum, int maximum, int invalidCodeIndex, int invalidResponseIndex)
        {
            int firstCandidateIndex = Math.Max(0, invalidCodeIndex - 48);
            int invalidResponseOffset = instructions[invalidResponseIndex].Offset;
            int normalizedMaximum = maximum - minimum;

            for (int index = firstCandidateIndex; index < invalidCodeIndex; index++)
            {
                IlInstruction instruction = instructions[index];
                if (instruction.OpCode.FlowControl != FlowControl.Cond_Branch || instruction.Operand is not int targetOffset)
                    continue;
                if (!InstructionWindowLoadsFieldAndConstant(instructions, requestGenderField, minimum, index, maxInstructionsBack: 10))
                    continue;
                if (!InstructionWindowHasConstant(instructions, normalizedMaximum, index, maxInstructionsBack: 10))
                    continue;
                if (!InstructionWindowHasOpCode(instructions, OpCodes.Sub, index, maxInstructionsBack: 10))
                    continue;

                bool targetReachesInvalidResponse = targetOffset > instruction.Offset && targetOffset <= invalidResponseOffset;
                bool targetSkipsInvalidResponse = targetOffset > invalidResponseOffset;
                if ((targetSkipsInvalidResponse && IsLessThanOrEqualUnsignedBranch(instruction.OpCode))
                    || (targetReachesInvalidResponse && IsGreaterThanUnsignedBranch(instruction.OpCode)))
                    return true;
            }

            return false;
        }

        private static bool InstructionWindowLoadsFieldAndConstant(List<IlInstruction> instructions, FieldInfo field, int expectedValue, int endIndex, int maxInstructionsBack)
        {
            bool loadedField = false;
            bool loadedConstant = false;
            int firstIndex = Math.Max(0, endIndex - maxInstructionsBack);

            for (int index = firstIndex; index < endIndex; index++)
            {
                if (instructions[index].Operand is FieldInfo loadedFieldInfo && FieldsMatch(loadedFieldInfo, field))
                    loadedField = true;
                if (LdcI4Value(instructions[index]) == expectedValue)
                    loadedConstant = true;
            }

            return loadedField && loadedConstant;
        }

        private static bool InstructionWindowHasConstant(List<IlInstruction> instructions, int expectedValue, int endIndex, int maxInstructionsBack)
        {
            int firstIndex = Math.Max(0, endIndex - maxInstructionsBack);
            for (int index = firstIndex; index < endIndex; index++)
            {
                if (LdcI4Value(instructions[index]) == expectedValue)
                    return true;
            }

            return false;
        }

        private static bool InstructionWindowHasOpCode(List<IlInstruction> instructions, OpCode opCode, int endIndex, int maxInstructionsBack)
        {
            int firstIndex = Math.Max(0, endIndex - maxInstructionsBack);
            for (int index = firstIndex; index < endIndex; index++)
            {
                if (instructions[index].OpCode == opCode)
                    return true;
            }

            return false;
        }

        private static bool HasMethodCallComparedToConstantBefore(List<IlInstruction> instructions, MethodInfo method, int expectedValue, int endIndex)
        {
            for (int index = 0; index < endIndex; index++)
            {
                if (instructions[index].Operand is not MethodBase calledMethod || !MethodsMatch(calledMethod, method))
                    continue;

                bool loadedConstant = false;
                bool comparedOrBranched = false;
                int lastIndex = Math.Min(endIndex, index + 10);
                for (int scanIndex = index + 1; scanIndex < lastIndex; scanIndex++)
                {
                    if (LdcI4Value(instructions[scanIndex]) == expectedValue)
                        loadedConstant = true;
                    if (IsComparisonOrConditionalBranch(instructions[scanIndex].OpCode))
                        comparedOrBranched = true;
                }

                if (loadedConstant && comparedOrBranched)
                    return true;
            }

            return false;
        }

        private static bool HasMethodCallStrictlyPositiveComparisonBefore(List<IlInstruction> instructions, MethodInfo method, int endIndex)
        {
            for (int index = 0; index < endIndex; index++)
            {
                if (instructions[index].Operand is not MethodBase calledMethod || !MethodsMatch(calledMethod, method))
                    continue;

                bool loadedZero = false;
                int lastIndex = Math.Min(endIndex, index + 12);
                for (int scanIndex = index + 1; scanIndex < lastIndex; scanIndex++)
                {
                    if (LdcI4Value(instructions[scanIndex]) == 0)
                    {
                        loadedZero = true;
                        continue;
                    }

                    if (!loadedZero)
                        continue;

                    OpCode opCode = instructions[scanIndex].OpCode;

                    if (opCode == OpCodes.Cgt
                        || IsGreaterThanBranch(opCode)
                        || IsLessThanOrEqualBranch(opCode))
                        return true;
                }
            }

            return false;
        }

        private static bool HasSameGenderComparisonBefore(List<IlInstruction> instructions, MethodInfo playerGenderGetter, FieldInfo requestGenderField, int endIndex)
        {
            for (int index = 0; index < endIndex; index++)
            {
                if (instructions[index].Operand is not MethodBase calledMethod || !MethodsMatch(calledMethod, playerGenderGetter))
                    continue;

                bool loadedRequestGender = false;
                bool comparedOrBranched = false;
                int lastIndex = Math.Min(endIndex, index + 20);
                for (int scanIndex = index + 1; scanIndex < lastIndex; scanIndex++)
                {
                    if (instructions[scanIndex].Operand is FieldInfo loadedField && FieldsMatch(loadedField, requestGenderField))
                        loadedRequestGender = true;
                    if (IsComparisonOrConditionalBranch(instructions[scanIndex].OpCode))
                        comparedOrBranched = true;
                }

                if (loadedRequestGender && comparedOrBranched)
                    return true;
            }

            return false;
        }

        private static int FindCallIndex(List<IlInstruction> instructions, MethodInfo target, int startIndex)
        {
            for (int index = startIndex; index < instructions.Count; index++)
            {
                if (instructions[index].Operand is MethodBase calledMethod && MethodsMatch(calledMethod, target))
                    return index;
            }

            return -1;
        }

        private static int FindMethodCallWithRecentConstants(List<IlInstruction> instructions, MethodInfo target, params int[] expectedValues)
        {
            for (int index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].Operand is not MethodBase calledMethod || !MethodsMatch(calledMethod, target))
                    continue;

                bool foundAllValues = true;
                foreach (int expectedValue in expectedValues)
                {
                    if (!InstructionWindowHasConstant(instructions, expectedValue, index, maxInstructionsBack: 12))
                    {
                        foundAllValues = false;
                        break;
                    }
                }

                if (foundAllValues)
                    return index;
            }

            return -1;
        }

        private static int FindFieldLoadIndex(List<IlInstruction> instructions, FieldInfo field, int startIndex)
        {
            for (int index = startIndex; index < instructions.Count; index++)
            {
                IlInstruction instruction = instructions[index];
                if ((instruction.OpCode == OpCodes.Ldfld || instruction.OpCode == OpCodes.Ldflda)
                    && instruction.Operand is FieldInfo loadedField
                    && FieldsMatch(loadedField, field))
                    return index;
            }

            return -1;
        }

        private static int FindFieldAssignmentIndex(List<IlInstruction> instructions, FieldInfo field, int startIndex, int endIndex)
        {
            int firstIndex = Math.Max(0, startIndex);
            int lastIndex = Math.Min(instructions.Count - 1, endIndex);
            for (int index = firstIndex; index <= lastIndex; index++)
            {
                IlInstruction instruction = instructions[index];
                if (instruction.OpCode == OpCodes.Stfld
                    && instruction.Operand is FieldInfo assignedField
                    && FieldsMatch(assignedField, field))
                    return index;
            }

            return -1;
        }

        private static void AssertFieldAssignmentUsesRecentCall(List<IlInstruction> instructions, int assignmentIndex, MethodInfo sourceMethod, string name)
        {
            for (int previousIndex = assignmentIndex - 1; previousIndex >= 0 && previousIndex >= assignmentIndex - 12; previousIndex--)
            {
                if (instructions[previousIndex].Operand is MethodBase calledMethod && MethodsMatch(calledMethod, sourceMethod))
                    return;
            }

            throw new InvalidDataException($"{name}: expected assignment to use {sourceMethod.DeclaringType?.FullName}.{sourceMethod.Name}.");
        }

        private static void AssertSetterAssignedConstantBetween(List<IlInstruction> instructions, MethodInfo setter, int expectedValue, int startIndex, int endIndex, string name)
        {
            for (int index = startIndex; index <= endIndex; index++)
            {
                if (instructions[index].Operand is not MethodBase calledMethod || !MethodsMatch(calledMethod, setter))
                    continue;
                if (InstructionWindowHasConstant(instructions, expectedValue, index, maxInstructionsBack: 8))
                    return;
            }

            throw new InvalidDataException($"{name}: expected {setter.DeclaringType?.FullName}.{setter.Name} to be assigned {expectedValue}.");
        }

        private static bool IsComparisonOrConditionalBranch(OpCode opCode)
        {
            return opCode.FlowControl == FlowControl.Cond_Branch
                || opCode == OpCodes.Ceq
                || opCode == OpCodes.Cgt
                || opCode == OpCodes.Cgt_Un
                || opCode == OpCodes.Clt
                || opCode == OpCodes.Clt_Un;
        }

        private static bool IsLessThanBranch(OpCode opCode)
        {
            return opCode == OpCodes.Blt || opCode == OpCodes.Blt_S;
        }

        private static bool IsGreaterThanBranch(OpCode opCode)
        {
            return opCode == OpCodes.Bgt || opCode == OpCodes.Bgt_S;
        }

        private static bool IsGreaterThanUnsignedBranch(OpCode opCode)
        {
            return opCode == OpCodes.Bgt_Un || opCode == OpCodes.Bgt_Un_S;
        }

        private static bool IsLessThanOrEqualBranch(OpCode opCode)
        {
            return opCode == OpCodes.Ble || opCode == OpCodes.Ble_S;
        }

        private static bool IsLessThanOrEqualUnsignedBranch(OpCode opCode)
        {
            return opCode == OpCodes.Ble_Un || opCode == OpCodes.Ble_Un_S;
        }

        private static bool IsGreaterThanOrEqualBranch(OpCode opCode)
        {
            return opCode == OpCodes.Bge || opCode == OpCodes.Bge_S;
        }

        private static int FindFieldAssignmentIndex(List<IlInstruction> instructions, FieldInfo field, int expectedValue)
        {
            for (int index = 0; index < instructions.Count; index++)
            {
                IlInstruction instruction = instructions[index];
                if (instruction.OpCode != OpCodes.Stfld || instruction.Operand is not FieldInfo assignedField || !FieldsMatch(assignedField, field))
                    continue;

                for (int previousIndex = index - 1; previousIndex >= 0 && previousIndex >= index - 6; previousIndex--)
                {
                    if (LdcI4Value(instructions[previousIndex]) == expectedValue)
                        return index;
                }
            }

            return -1;
        }

        private static int FindGenericCallIndex(List<IlInstruction> instructions, MethodInfo genericMethodDefinition, Type genericArgument, int startIndex)
        {
            for (int index = startIndex; index < instructions.Count; index++)
            {
                if (instructions[index].Operand is MethodBase calledMethod && GenericMethodsMatch(calledMethod, genericMethodDefinition, genericArgument))
                    return index;
            }

            return -1;
        }

        private static int FindGenericCallIndex(List<IlInstruction> instructions, MethodInfo genericMethodDefinition, int startIndex)
        {
            for (int index = startIndex; index < instructions.Count; index++)
            {
                if (instructions[index].Operand is not MethodInfo calledMethod || !calledMethod.IsGenericMethod)
                    continue;

                MethodInfo calledDefinition = calledMethod.IsGenericMethodDefinition
                    ? calledMethod
                    : calledMethod.GetGenericMethodDefinition();
                if (MethodsMatch(calledDefinition, genericMethodDefinition))
                    return index;
            }

            return -1;
        }

        private static int FindGenericMethodCallByNameIndex(List<IlInstruction> instructions, Type declaringType, string methodName, Type genericArgument, int startIndex)
        {
            for (int index = startIndex; index < instructions.Count; index++)
            {
                if (instructions[index].Operand is not MethodInfo calledMethod || !calledMethod.IsGenericMethod || calledMethod.Name != methodName)
                    continue;

                Type? candidateDeclaringType = calledMethod.DeclaringType;
                if (candidateDeclaringType is null || candidateDeclaringType != declaringType)
                    continue;

                Type[] genericArguments = calledMethod.GetGenericArguments();
                if (genericArguments.Length == 1 && genericArguments[0] == genericArgument)
                    return index;
            }

            return -1;
        }

        private static int FindFirstInventoryMutationIndex(List<IlInstruction> instructions, MethodInfo inventoryDo, MethodInfo inventorySave)
        {
            for (int index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].Operand is not MethodInfo calledMethod)
                    continue;

                if (MethodsMatch(calledMethod, inventoryDo)
                    || MethodsMatch(calledMethod, inventorySave)
                    || CallsTargetTransitively(calledMethod, inventorySave, []))
                    return index;
            }

            return -1;
        }

        private static bool HasConditionalBranchGuard(List<IlInstruction> instructions, int guardedStartIndex, int guardedEndIndex)
        {
            const int maxInstructionsToInspect = 16;
            int firstCandidateIndex = Math.Max(0, guardedStartIndex - maxInstructionsToInspect);
            int guardedStartOffset = instructions[guardedStartIndex].Offset;
            int guardedEndOffset = instructions[guardedEndIndex].Offset;

            for (int index = guardedStartIndex - 1; index >= firstCandidateIndex; index--)
            {
                IlInstruction instruction = instructions[index];
                if (instruction.OpCode.FlowControl != FlowControl.Cond_Branch || instruction.Operand is not int targetOffset)
                    continue;

                if ((targetOffset >= guardedStartOffset && targetOffset <= guardedEndOffset) || targetOffset > guardedEndOffset)
                    return true;
            }

            return false;
        }

        private static bool PathExits(List<IlInstruction> instructions, int startIndex)
        {
            IlInstruction? nextMeaningfulInstruction = NextMeaningfulInstruction(instructions, startIndex);
            if (nextMeaningfulInstruction is null)
                return false;
            if (nextMeaningfulInstruction.Value.OpCode.FlowControl == FlowControl.Return)
                return true;
            if (nextMeaningfulInstruction.Value.OpCode.FlowControl != FlowControl.Branch || nextMeaningfulInstruction.Value.Operand is not int targetOffset)
                return false;

            int targetIndex = instructions.FindIndex(instruction => instruction.Offset == targetOffset);
            if (targetIndex < 0)
                return false;

            IlInstruction? branchTargetInstruction = NextMeaningfulInstruction(instructions, targetIndex);
            return branchTargetInstruction?.OpCode.FlowControl == FlowControl.Return;
        }

        private static IlInstruction? NextMeaningfulInstruction(List<IlInstruction> instructions, int startIndex)
        {
            for (int index = startIndex; index < instructions.Count; index++)
            {
                if (instructions[index].OpCode != OpCodes.Nop)
                    return instructions[index];
            }

            return null;
        }

        private static IEnumerable<IlInstruction> ReadIlInstructions(MethodInfo method)
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray()
                ?? throw new InvalidDataException($"{method.DeclaringType?.FullName}.{method.Name}: method body is unavailable.");
            Module module = method.Module;
            Type[] typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
            Type[] methodArguments = method.GetGenericArguments();

            for (int offset = 0; offset < il.Length;)
            {
                int instructionOffset = offset;
                OpCode opCode = ReadOpCode(il, ref offset);
                object? operand = ReadOperand(il, ref offset, opCode, module, typeArguments, methodArguments);
                yield return new IlInstruction(instructionOffset, opCode, operand);
            }
        }

        private static object? ReadOperand(byte[] il, ref int offset, OpCode opCode, Module module, Type[] typeArguments, Type[] methodArguments)
        {
            int operandOffset = offset;
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    return null;
                case OperandType.ShortInlineI:
                    offset += 1;
                    return (int)(sbyte)il[operandOffset];
                case OperandType.InlineI:
                    offset += 4;
                    return BitConverter.ToInt32(il, operandOffset);
                case OperandType.ShortInlineBrTarget:
                    offset += 1;
                    return offset + (sbyte)il[operandOffset];
                case OperandType.InlineBrTarget:
                    offset += 4;
                    return offset + BitConverter.ToInt32(il, operandOffset);
                case OperandType.InlineMethod:
                    offset += 4;
                    return ResolveToken(module, BitConverter.ToInt32(il, operandOffset), typeArguments, methodArguments);
                case OperandType.InlineField:
                    offset += 4;
                    return ResolveField(module, BitConverter.ToInt32(il, operandOffset), typeArguments, methodArguments);
                default:
                    offset += OperandSize(opCode.OperandType, il, operandOffset);
                    return null;
            }
        }

        private static MemberInfo? ResolveToken(Module module, int metadataToken, Type[] typeArguments, Type[] methodArguments)
        {
            try
            {
                return module.ResolveMethod(metadataToken, typeArguments, methodArguments);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static FieldInfo? ResolveField(Module module, int metadataToken, Type[] typeArguments, Type[] methodArguments)
        {
            try
            {
                return module.ResolveField(metadataToken, typeArguments, methodArguments);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static int? LdcI4Value(IlInstruction instruction)
        {
            if (instruction.OpCode == OpCodes.Ldc_I4_M1)
                return -1;
            if (instruction.OpCode == OpCodes.Ldc_I4_0)
                return 0;
            if (instruction.OpCode == OpCodes.Ldc_I4_1)
                return 1;
            if (instruction.OpCode == OpCodes.Ldc_I4_2)
                return 2;
            if (instruction.OpCode == OpCodes.Ldc_I4_3)
                return 3;
            if (instruction.OpCode == OpCodes.Ldc_I4_4)
                return 4;
            if (instruction.OpCode == OpCodes.Ldc_I4_5)
                return 5;
            if (instruction.OpCode == OpCodes.Ldc_I4_6)
                return 6;
            if (instruction.OpCode == OpCodes.Ldc_I4_7)
                return 7;
            if (instruction.OpCode == OpCodes.Ldc_I4_8)
                return 8;
            if (instruction.OpCode is { } opCode && (opCode == OpCodes.Ldc_I4 || opCode == OpCodes.Ldc_I4_S) && instruction.Operand is int value)
                return value;

            return null;
        }

        private static bool FieldsMatch(FieldInfo candidate, FieldInfo target)
        {
            return candidate.Module == target.Module && candidate.MetadataToken == target.MetadataToken;
        }

        private readonly record struct IlInstruction(int Offset, OpCode OpCode, object? Operand);

        private static bool NextMeaningfulOpCodeIsConditionalBranch(byte[] il, int offset)
        {
            const int maxInstructionsToInspect = 8;
            int inspectedInstructions = 0;

            while (offset < il.Length && inspectedInstructions < maxInstructionsToInspect)
            {
                OpCode opCode = ReadOpCode(il, ref offset);
                offset += OperandSize(opCode.OperandType, il, offset);
                if (opCode == OpCodes.Nop)
                    continue;

                inspectedInstructions++;
                if (opCode.FlowControl == FlowControl.Cond_Branch)
                    return true;
                if (opCode.OperandType == OperandType.InlineMethod)
                    return false;
            }

            return false;
        }

        private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray()
                ?? throw new InvalidDataException($"{method.DeclaringType?.FullName}.{method.Name}: method body is unavailable.");
            Module module = method.Module;
            Type[] typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
            Type[] methodArguments = method.GetGenericArguments();

            for (int offset = 0; offset < il.Length;)
            {
                OpCode opCode = ReadOpCode(il, ref offset);
                if (opCode.OperandType == OperandType.InlineMethod)
                {
                    int metadataToken = BitConverter.ToInt32(il, offset);
                    offset += 4;

                    MethodBase? calledMethod;
                    try
                    {
                        calledMethod = module.ResolveMethod(metadataToken, typeArguments, methodArguments);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    if (calledMethod is not null)
                        yield return calledMethod;
                }
                else
                {
                    offset += OperandSize(opCode.OperandType, il, offset);
                }
            }
        }

        private static OpCode ReadOpCode(byte[] il, ref int offset)
        {
            byte value = il[offset++];
            if (value != 0xFE)
                return SingleByteOpCodes[value];

            return MultiByteOpCodes[il[offset++]];
        }

        private static int OperandSize(OperandType operandType, byte[] il, int offset)
        {
            return operandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, offset) * 4),
                _ => throw new InvalidDataException($"Unsupported IL operand type {operandType}.")
            };
        }

        private static bool GenericMethodsMatch(MethodBase candidate, MethodInfo genericMethodDefinition, Type genericArgument)
        {
            if (candidate is not MethodInfo candidateMethod || !candidateMethod.IsGenericMethod)
                return false;

            MethodInfo candidateDefinition = candidateMethod.IsGenericMethodDefinition
                ? candidateMethod
                : candidateMethod.GetGenericMethodDefinition();
            if (candidateDefinition.Module != genericMethodDefinition.Module || candidateDefinition.MetadataToken != genericMethodDefinition.MetadataToken)
                return false;

            Type[] genericArguments = candidateMethod.GetGenericArguments();
            return genericArguments.Length == 1 && genericArguments[0] == genericArgument;
        }

        private static string FormatGenericMethod(MethodInfo genericMethodDefinition, Type genericArgument)
        {
            return $"{genericMethodDefinition.DeclaringType?.FullName}.{genericMethodDefinition.Name}<{genericArgument.FullName}>";
        }

        private static bool MethodsMatch(MethodBase candidate, MethodInfo target)
        {
            return candidate.Module == target.Module && candidate.MetadataToken == target.MetadataToken;
        }

        private static string MethodKey(MethodBase method)
        {
            return $"{method.Module.ModuleVersionId:N}:{method.MetadataToken}";
        }

        private static readonly OpCode[] SingleByteOpCodes = BuildOpCodeTable(multiByte: false);
        private static readonly OpCode[] MultiByteOpCodes = BuildOpCodeTable(multiByte: true);

        private static OpCode[] BuildOpCodeTable(bool multiByte)
        {
            OpCode[] table = new OpCode[0x100];
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is not OpCode opCode)
                    continue;

                ushort value = (ushort)opCode.Value;
                if (multiByte)
                {
                    if ((value & 0xFF00) == 0xFE00)
                        table[value & 0xFF] = opCode;
                }
                else if (value < 0x100)
                {
                    table[value] = opCode;
                }
            }

            return table;
        }

        private static void ValidateCurrentClientNoticeFixtures()
        {
            JObject loginNotice = JObject.Parse(File.ReadAllText(ResourcePath("Configs", "Notices", "4.5.0", "LoginNotice.json")));
            AssertEqual("6a194a09f1b4a1288a8994ca", loginNotice.Value<string>("Id"), "LoginNotice Id");
            AssertEqual(1780042249545, loginNotice.Value<long>("ModifyTime"), "LoginNotice ModifyTime");
            AssertEqual("client/notice/html/6a1949e1f1b4a1288a8994c9.html", loginNotice.Value<string>("HtmlUrl"), "LoginNotice HtmlUrl");

            JObject scrollTextNotice = JObject.Parse(File.ReadAllText(ResourcePath("Configs", "Notices", "4.5.0", "ScrollTextNotice.json")));
            AssertEqual("6a194a9df1b4a1288a8994cb", scrollTextNotice.Value<string>("Id"), "ScrollTextNotice Id");
            AssertEqual(300, scrollTextNotice.Value<int>("ScrollInterval"), "ScrollTextNotice ScrollInterval");
            if (!scrollTextNotice.Value<string>("Content")!.Contains("Homecoming Voyage", StringComparison.Ordinal))
                throw new InvalidDataException("ScrollTextNotice content is not the current 4.5.0 notice.");

            JObject scrollPicNotice = JObject.Parse(File.ReadAllText(ResourcePath("Configs", "Notices", "4.5.0", "ScrollPicNotice.json")));
            AssertEqual("6a1e16a2f1b4a13fd8bf490e", scrollPicNotice.Value<string>("Id"), "ScrollPicNotice Id");
            AssertEqual(10, scrollPicNotice.Value<JArray>("Content")!.Count, "ScrollPicNotice content count");

            JArray gameNotices = JArray.Parse(File.ReadAllText(ResourcePath("Configs", "Notices", "4.5.0", "GameNotice.json")));
            AssertEqual(16, gameNotices.Count, "GameNotice count");
            AssertEqual("EDEN BROADCAST", gameNotices[0]!.Value<string>("Title"), "GameNotice first title");
        }

        private static async Task ValidateCurrentClientNoticeEndpoints()
        {
            await using WebApplication app = CreateConfigControllerTestApp();
            await app.StartAsync();

            try
            {
                using HttpClient client = new()
                {
                    BaseAddress = new Uri(BoundAddress(app))
                };

                string[] endpoints =
                [
                    "/prod/client/notice/config/prod-encdn-tx/com.kurogame.pc.punishing.grayraven.en/4.5.0/SecondMenuNotice.json",
                    "/prod/client/notice/config/prod-encdn-tx/com.kurogame.pc.punishing.grayraven.en/4.5.0/PopUpPicNotice.json",
                ];

                foreach (string endpoint in endpoints)
                {
                    using HttpResponseMessage response = await client.GetAsync(endpoint);
                    string body = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidDataException($"{endpoint}: expected HTTP 200, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");

                    AssertCurrentClientNoticePayload(JObject.Parse(body), endpoint);
                }
            }
            finally
            {
                await app.StopAsync();
            }
        }

        private static void AssertCurrentClientNoticePayload(JObject payload, string endpoint)
        {
            _ = RequiredNonEmptyString(payload, "Id", endpoint);
            _ = RequiredValue<long>(payload, "ModifyTime", JTokenType.Integer, endpoint);
            _ = RequiredToken(payload, "Content", JTokenType.Array, endpoint);
            _ = RequiredToken(payload, "LoginPlatformList", JTokenType.Array, endpoint);
        }

        private static void ValidateSteamClientConfig()
        {
            Type configController = Type.GetType("AscNet.SDKServer.Controllers.ConfigController, AscNet.SDKServer", throwOnError: true)!;

            MethodInfo getPackageConfig = configController.GetMethod("GetPackageConfig", BindingFlags.NonPublic | BindingFlags.Static)!;
            object packageConfig = getPackageConfig.Invoke(null, ["com.kurogame.pc.punishing.grayraven.en", true])!;
            AssertEqual("http://prod-encdn-tx.kurogame.net/prod", packageConfig.GetType().GetField("Item1")!.GetValue(packageConfig), "Steam PrimaryCdns");
            AssertEqual("http://prod-encdn-aliyun.kurogame.net/prod", packageConfig.GetType().GetField("Item2")!.GetValue(packageConfig), "Steam SecondaryCdns");
            AssertEqual(205, packageConfig.GetType().GetField("Item3")!.GetValue(packageConfig), "Steam Channel");

            MethodInfo addCurrentClientConfig = configController.GetMethod("AddCurrentClientConfig", BindingFlags.NonPublic | BindingFlags.Static)!;
            List<RemoteConfig> remoteConfigs = new();
            ServerVersionConfig versionConfig = new()
            {
                DocumentVersion = "4.5.12",
                LaunchModuleVersion = "4.5.12",
                IndexMd5 = "c5d4baac85a6e37b8109ea43dc045d31",
                IndexSha1 = "23aa5943c6b89d62ed6c1acea573e6ac2970b4bf",
                LaunchIndexSha1 = "b4ae904215964fe6dfc2d3c5f04bfd1ccf53b659"
            };
            addCurrentClientConfig.Invoke(null, [remoteConfigs, "com.kurogame.pc.punishing.grayraven.en", "4.5.0", versionConfig, "http://127.0.0.1:8080"]);

            AssertEqual("205", ConfigValue(remoteConfigs, "Channel"), "Steam Channel config");
            AssertEqual("4.5.12", ConfigValue(remoteConfigs, "DocumentVersion"), "Steam DocumentVersion config");
            AssertEqual("4.5.12", ConfigValue(remoteConfigs, "LaunchModuleVersion"), "Steam LaunchModuleVersion config");
            AssertEqual("http://127.0.0.1:8080/api/XPay/KuroPayResult", ConfigValue(remoteConfigs, "KuroPayCallbackUrl"), "KuroPayCallbackUrl");
            AssertEqual("http://127.0.0.1:8080/api/XPay/KuroPayResult", ConfigValue(remoteConfigs, "PcPayCallbackUrl"), "PcPayCallbackUrl");
        }

        private const string KuroSdkDummyEmail = "krsdk-test@ascnet.local";
        private const string KuroSdkDummyToken = "krsdk-local-token";
        private const string KuroSdkDummySteamId = "76561198000000000";
        private const string KuroSdkDummyCuid = "steam-76561198000000000";
        private const string KuroSdkDummyUsername = "SteamUser76561198000000000";
        private const int KuroSdkDummyLoginType = 37;
        private const string KuroSdkDummyMark = "ascnet-steam-login-mark";

        private static async Task ValidateKuroSdkCompatibilityEndpoints()
        {
            string? previousPublicHttpOrigin = Environment.GetEnvironmentVariable("ASCNET_PUBLIC_HTTP_ORIGIN");
            Environment.SetEnvironmentVariable("ASCNET_PUBLIC_HTTP_ORIGIN", null);
            await using WebApplication app = CreateKuroSdkTestApp();
            await app.StartAsync();

            try
            {
                using HttpClient client = new()
                {
                    BaseAddress = new Uri(BoundAddress(app))
                };

                KuroSdkEndpointContract[] endpoints =
                [
                    new("/sdkcom/v2/login/emailPwd.lg", AssertKuroSdkLoginData),
                    new("/sdkcom/v2/login/third/steam.lg", AssertKuroSdkLoginData),
                    new("/sdkcom/v2/login/auto.lg", AssertKuroSdkLoginData),
                    new("/sdkcom/v2/login/real-name/login.lg", AssertKuroSdkLoginData),
                    new("/sdkcom/v2/login/preambleCode.lg", AssertKuroSdkLoginData),
                    new("/sdkcom/v2/auth/getToken.lg", AssertKuroSdkAccessTokenData),
                    new("/sdkcom/v2/user/oauth/code/generate.lg", AssertKuroSdkOauthCodeData),
                    new("/sdkcom/v2/user/game/role.lg", AssertKuroSdkEmptyData),
                    new("/sdkcom/v2/heartbeat/tokenCheck.lg", AssertKuroSdkEmptyData),
                    new("/sdkcom/v2/bind/device/status.lg", AssertKuroSdkEmptyData),
                    new("/sdkcom/v2/real-name-info/check.lg", AssertKuroSdkRealNameCheckData),
                ];

                foreach (KuroSdkEndpointContract endpoint in endpoints)
                {
                    using HttpRequestMessage request = new(HttpMethod.Post, $"{endpoint.Path}?{KuroSdkHandoffQuery()}")
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                    using HttpResponseMessage response = await client.SendAsync(request);
                    string body = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidDataException($"{endpoint.Path}: expected HTTP 200, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");

                    JObject payload = JObject.Parse(body);
                    AssertKuroSdkSuccessEnvelope(payload, endpoint.Path);
                    endpoint.AssertData(RequiredObject(payload, "data", endpoint.Path), endpoint.Path);
                }

                using HttpRequestMessage formLoginRequest = new(HttpMethod.Post, "/sdkcom/v2/login/third/steam.lg")
                {
                    Content = new FormUrlEncodedContent(KuroSdkHandoffFields())
                };
                using HttpResponseMessage formLoginResponse = await client.SendAsync(formLoginRequest);
                string formLoginBody = await formLoginResponse.Content.ReadAsStringAsync();

                if (formLoginResponse.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException($"/sdkcom/v2/login/third/steam.lg form: expected HTTP 200, got {(int)formLoginResponse.StatusCode} {formLoginResponse.StatusCode}. Body: {formLoginBody}");

                JObject formLoginPayload = JObject.Parse(formLoginBody);
                AssertKuroSdkSuccessEnvelope(formLoginPayload, "/sdkcom/v2/login/third/steam.lg form");
                AssertKuroSdkLoginData(RequiredObject(formLoginPayload, "data", "/sdkcom/v2/login/third/steam.lg form"), "/sdkcom/v2/login/third/steam.lg form");

                using HttpRequestMessage markRequest = new(HttpMethod.Post, $"/sdkcom/v2/login/third/pc/mark.lg?{KuroSdkHandoffQuery(mark: KuroSdkDummyMark)}")
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                using HttpResponseMessage markResponse = await client.SendAsync(markRequest);
                string markBody = await markResponse.Content.ReadAsStringAsync();

                if (markResponse.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException($"/sdkcom/v2/login/third/pc/mark.lg: expected HTTP 200, got {(int)markResponse.StatusCode} {markResponse.StatusCode}. Body: {markBody}");

                JObject markPayload = JObject.Parse(markBody);
                AssertKuroSdkSuccessEnvelope(markPayload, "/sdkcom/v2/login/third/pc/mark.lg");
                AssertKuroSdkThirdLoginMarkData(RequiredObject(markPayload, "data", "/sdkcom/v2/login/third/pc/mark.lg"), "/sdkcom/v2/login/third/pc/mark.lg");

                using HttpRequestMessage browserRequest = new(HttpMethod.Post, $"/sdkcom/v2/login/third/pc/browser.lg?{KuroSdkHandoffQuery(mark: KuroSdkDummyMark)}")
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                using HttpResponseMessage browserResponse = await client.SendAsync(browserRequest);
                string browserBody = await browserResponse.Content.ReadAsStringAsync();

                if (browserResponse.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException($"/sdkcom/v2/login/third/pc/browser.lg: expected HTTP 200, got {(int)browserResponse.StatusCode} {browserResponse.StatusCode}. Body: {browserBody}");

                JObject browserPayload = JObject.Parse(browserBody);
                AssertKuroSdkSuccessCodeAndMessage(browserPayload, "/sdkcom/v2/login/third/pc/browser.lg");
                string browserUrl = RequiredValue<string>(browserPayload, "url", JTokenType.String, "/sdkcom/v2/login/third/pc/browser.lg");
                AssertEqual(browserUrl, RequiredValue<string>(browserPayload, "data", JTokenType.String, "/sdkcom/v2/login/third/pc/browser.lg"), "/sdkcom/v2/login/third/pc/browser.lg data");
                AssertKuroSdkLocalLoginUrl(browserUrl, "/sdkcom/v2/login/third/pc/browser.lg url", KuroSdkDummyMark, KuroSdkDummyLoginType.ToString());

                string[] systemConfigEndpoints =
                [
                    "/sdkcom/v2/sys/europe/config.lg",
                    "/sdkcom/v2/sys/conf.lg",
                ];

                foreach (string systemConfigEndpoint in systemConfigEndpoints)
                {
                    using HttpResponseMessage systemConfigResponse = await client.GetAsync(systemConfigEndpoint);
                    string systemConfigBody = await systemConfigResponse.Content.ReadAsStringAsync();

                    if (systemConfigResponse.StatusCode != HttpStatusCode.OK)
                        throw new InvalidDataException($"{systemConfigEndpoint}: expected HTTP 200, got {(int)systemConfigResponse.StatusCode} {systemConfigResponse.StatusCode}. Body: {systemConfigBody}");

                    JObject payload = JObject.Parse(systemConfigBody);
                    AssertKuroSdkSuccessEnvelope(payload, systemConfigEndpoint);
                    AssertKuroSdkSystemConfigData(RequiredObject(payload, "data", systemConfigEndpoint), systemConfigEndpoint);
                }

                using HttpResponseMessage playerConfigResponse = await client.GetAsync("/sdkcom/v2/sys/player-config.json");
                string playerConfigBody = await playerConfigResponse.Content.ReadAsStringAsync();

                if (playerConfigResponse.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException($"/sdkcom/v2/sys/player-config.json: expected HTTP 200, got {(int)playerConfigResponse.StatusCode} {playerConfigResponse.StatusCode}. Body: {playerConfigBody}");

                AssertKuroSdkSystemConfigData(JObject.Parse(playerConfigBody), "/sdkcom/v2/sys/player-config.json");
            }
            finally
            {
                await app.StopAsync();
                Environment.SetEnvironmentVariable("ASCNET_PUBLIC_HTTP_ORIGIN", previousPublicHttpOrigin);
            }
        }

        private static WebApplication CreateKuroSdkTestApp()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = ["--urls", "http://127.0.0.1:0"]
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            WebApplication app = builder.Build();
            Type kuroSdkController = Type.GetType("AscNet.SDKServer.Controllers.KuroSdkController, AscNet.SDKServer", throwOnError: true)!;
            kuroSdkController.GetMethod("Register", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [app]);
            return app;
        }

        private static WebApplication CreateConfigControllerTestApp()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = ["--urls", "http://127.0.0.1:0"]
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            WebApplication app = builder.Build();
            Type configController = Type.GetType("AscNet.SDKServer.Controllers.ConfigController, AscNet.SDKServer", throwOnError: true)!;
            configController.GetMethod("Register", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [app]);
            return app;
        }

        private static string BoundAddress(WebApplication app)
        {
            IServerAddressesFeature? addressesFeature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            string? address = addressesFeature?.Addresses.FirstOrDefault(address => address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));

            if (address is null)
                throw new InvalidDataException("Test server did not publish a loopback HTTP address.");

            return address;
        }

        private static void AssertKuroSdkSuccessEnvelope(JObject payload, string endpoint)
        {
            AssertKuroSdkSuccessCodeAndMessage(payload, endpoint);
            _ = RequiredObject(payload, "data", endpoint);
        }

        private static void AssertKuroSdkLoginData(JObject data, string endpoint)
        {
            _ = RequiredValue<int>(data, "id", JTokenType.Integer, endpoint);
            AssertEqual(KuroSdkDummyCuid, RequiredNonEmptyString(data, "cuid", endpoint), $"{endpoint} cuid");
            AssertEqual(KuroSdkDummyUsername, RequiredNonEmptyString(data, "username", endpoint), $"{endpoint} username");
            AssertEqual(KuroSdkDummyLoginType, RequiredValue<int>(data, "loginType", JTokenType.Integer, endpoint), $"{endpoint} loginType");
            _ = RequiredNonEmptyString(data, "code", endpoint);
            AssertEqual(KuroSdkDummyEmail, RequiredNonEmptyString(data, "email", endpoint), $"{endpoint} email");
            AssertEqual(KuroSdkDummyToken, RequiredNonEmptyString(data, "autoToken", endpoint), $"{endpoint} autoToken");
            AssertEqual(KuroSdkDummyToken, RequiredNonEmptyString(data, "token", endpoint), $"{endpoint} token");
            _ = RequiredValue<int>(data, "bindDevStat", JTokenType.Integer, endpoint);
            _ = RequiredValue<int>(data, "idStat", JTokenType.Integer, endpoint);
            _ = RequiredValue<int>(data, "firstLgn", JTokenType.Integer, endpoint);
            _ = RequiredValue<string>(data, "bindDevMsg", JTokenType.String, endpoint);
            _ = RequiredValue<int>(data, "realNameMethod", JTokenType.Integer, endpoint);
            _ = RequiredValue<string>(data, "thirdNickName", JTokenType.String, endpoint);
            _ = RequiredValue<int>(data, "bindDevSwitch", JTokenType.Integer, endpoint);
            _ = RequiredValue<string>(data, "realNameUrl", JTokenType.String, endpoint);
            _ = RequiredValue<string>(data, "realNameKey", JTokenType.String, endpoint);
        }

        private static void AssertKuroSdkAccessTokenData(JObject data, string endpoint)
        {
            _ = RequiredNonEmptyString(data, "access_token", endpoint);
            _ = RequiredValue<int>(data, "expires_in", JTokenType.Integer, endpoint);
        }

        private static void AssertKuroSdkOauthCodeData(JObject data, string endpoint)
        {
            _ = RequiredNonEmptyString(data, "oauthCode", endpoint);
            _ = RequiredNonEmptyString(data, "code", endpoint);
        }
        private static void AssertKuroSdkThirdLoginMarkData(JObject data, string endpoint)
        {
            AssertEqual(1, RequiredValue<int>(data, "ready", JTokenType.Integer, endpoint), $"{endpoint} ready");
            AssertEqual(KuroSdkDummyMark, RequiredNonEmptyString(data, "mark", endpoint), $"{endpoint} mark");
        }



        private static void AssertKuroSdkSystemConfigData(JObject data, string endpoint)
        {
            JArray links = (JArray)RequiredToken(data, "link", JTokenType.Array, endpoint);
            if (links.Count == 0)
                throw new InvalidDataException($"{endpoint} link: expected at least one CDN entry.");
            if (links[0] is not JObject firstLink)
                throw new InvalidDataException($"{endpoint} link[0]: expected JSON Object, got {links[0].Type}.");
            _ = RequiredNonEmptyString(firstLink, "url", $"{endpoint} link[0]");
            _ = RequiredValue<int>(firstLink, "weight", JTokenType.Integer, $"{endpoint} link[0]");
            _ = RequiredValue<int>(data, "clientSwitch", JTokenType.Integer, endpoint);
            _ = RequiredNonEmptyString(data, "pcGeetestUrl", endpoint);
            _ = RequiredNonEmptyString(data, "accCenterUrl", endpoint);
            JObject clientUrlConfig = JObject.Parse(RequiredNonEmptyString(data, "clientUrl", endpoint));
            _ = RequiredNonEmptyString(clientUrlConfig, "pcGeetestUrl", $"{endpoint} clientUrl");
            _ = RequiredNonEmptyString(clientUrlConfig, "accCenterUrl", $"{endpoint} clientUrl");
            AssertKuroSdkLocalLoginUrl(RequiredNonEmptyString(clientUrlConfig, "pcThirdLoginUrl", $"{endpoint} clientUrl"), $"{endpoint} clientUrl pcThirdLoginUrl");
            _ = RequiredNonEmptyString(clientUrlConfig, "kefu", $"{endpoint} clientUrl");
            _ = RequiredNonEmptyString(clientUrlConfig, "kefuServ", $"{endpoint} clientUrl");
            _ = RequiredValue<int>(clientUrlConfig, "sobot", JTokenType.Integer, $"{endpoint} clientUrl");
            _ = RequiredNonEmptyString(clientUrlConfig, "sobotRedDotUrl", $"{endpoint} clientUrl");
            _ = RequiredNonEmptyString(clientUrlConfig, "googlePcAuthResultUrl", $"{endpoint} clientUrl");
            _ = RequiredNonEmptyString(clientUrlConfig, "emailSystemUrl", $"{endpoint} clientUrl");
            _ = RequiredNonEmptyString(clientUrlConfig, "bizsiren", $"{endpoint} clientUrl");
            AssertKuroSdkLocalLoginUrl(RequiredNonEmptyString(data, "pcThirdLoginUrl", endpoint), $"{endpoint} pcThirdLoginUrl");
            _ = RequiredValue<int>(data, "thirdLogin", JTokenType.Integer, endpoint);
            _ = RequiredNonEmptyString(data, "kefu", endpoint);
            _ = RequiredNonEmptyString(data, "kefuServ", endpoint);
            _ = RequiredValue<int>(data, "sobot", JTokenType.Integer, endpoint);
            _ = RequiredNonEmptyString(data, "sobotRedDotUrl", endpoint);
            _ = RequiredNonEmptyString(data, "googlePcAuthResultUrl", endpoint);
            _ = RequiredNonEmptyString(data, "emailSystemUrl", endpoint);
            _ = RequiredValue<int>(data, "webviewIgnoreCertErrorForWin", JTokenType.Integer, endpoint);
            _ = RequiredValue<int>(data, "disableAgreementRemainDlgForWin", JTokenType.Integer, endpoint);
            _ = RequiredValue<int>(data, "PayEventFixedForPCSteam", JTokenType.Integer, endpoint);
        }

        private static void AssertKuroSdkSuccessCodeAndMessage(JObject payload, string endpoint)
        {
            AssertEqual(0, RequiredValue<int>(payload, "code", JTokenType.Integer, endpoint), $"{endpoint} code");
            AssertEqual("success", RequiredValue<string>(payload, "msg", JTokenType.String, endpoint), $"{endpoint} msg");
        }

        private static void AssertKuroSdkLocalLoginUrl(string value, string name, string? expectedMark = null, string? expectedType = null)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                throw new InvalidDataException($"{name}: expected an absolute local SDK URL, got '{value}'.");

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"{name}: expected HTTP(S) local SDK URL, got '{value}'.");

            if (!uri.IsLoopback)
                throw new InvalidDataException($"{name}: expected loopback local SDK URL, got '{value}'.");

            AssertEqual("/sdkcom/v2/local/login", uri.AbsolutePath, name);

            if (expectedMark is not null || expectedType is not null)
            {
                Dictionary<string, string> query = ParseQueryString(uri.Query);
                if (expectedMark is not null)
                    AssertEqual(expectedMark, query.GetValueOrDefault("mark"), $"{name} mark");
                if (expectedType is not null)
                    AssertEqual(expectedType, query.GetValueOrDefault("type"), $"{name} type");
            }
        }


        private static void AssertKuroSdkEmptyData(JObject data, string endpoint)
        {
        }

        private static void AssertKuroSdkRealNameCheckData(JObject data, string endpoint)
        {
            _ = RequiredValue<int>(data, "realNameMethod", JTokenType.Integer, endpoint);
            _ = RequiredValue<string>(data, "realNameKey", JTokenType.String, endpoint);
            _ = RequiredValue<string>(data, "realNameUrl", JTokenType.String, endpoint);
        }

        private static JObject RequiredObject(JObject payload, string propertyName, string endpoint)
        {
            return (JObject)RequiredToken(payload, propertyName, JTokenType.Object, endpoint);
        }

        private static string RequiredNonEmptyString(JObject payload, string propertyName, string endpoint)
        {
            string value = RequiredValue<string>(payload, propertyName, JTokenType.String, endpoint);
            if (value.Length == 0)
                throw new InvalidDataException($"{endpoint} {propertyName}: expected non-empty string.");

            return value;
        }

        private static T RequiredValue<T>(JObject payload, string propertyName, JTokenType expectedType, string endpoint)
        {
            return RequiredToken(payload, propertyName, expectedType, endpoint).Value<T>()!;
        }

        private static JToken RequiredToken(JObject payload, string propertyName, JTokenType expectedType, string endpoint)
        {
            if (!payload.TryGetValue(propertyName, out JToken? token))
                throw new InvalidDataException($"{endpoint}: missing JSON field '{propertyName}'.");

            if (token.Type != expectedType)
                throw new InvalidDataException($"{endpoint} {propertyName}: expected JSON {expectedType}, got {token.Type}.");

            return token;
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            Dictionary<string, string> result = new();
            foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = part.IndexOf('=');
                string key = separator >= 0 ? part[..separator] : part;
                string value = separator >= 0 ? part[(separator + 1)..] : string.Empty;
                result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            }

            return result;
        }

        private static Dictionary<string, string> KuroSdkHandoffFields(string? url = null, string? mark = null)
        {
            Dictionary<string, string> fields = new()
            {
                ["email"] = KuroSdkDummyEmail,
                ["token"] = KuroSdkDummyToken,
                ["steamId"] = KuroSdkDummySteamId,
                ["cuid"] = KuroSdkDummyCuid,
                ["username"] = KuroSdkDummyUsername,
                ["loginType"] = KuroSdkDummyLoginType.ToString()
            };

            if (url is not null)
                fields["url"] = url;
            if (mark is not null)
                fields["mark"] = mark;

            return fields;
        }

        private static string KuroSdkHandoffQuery(string? url = null, string? mark = null)
        {
            return string.Join("&", KuroSdkHandoffFields(url, mark).Select(field => $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(field.Value)}"));
        }

        private sealed record KuroSdkEndpointContract(string Path, Action<JObject, string> AssertData);

        private static string ConfigValue(List<RemoteConfig> remoteConfigs, string key)
        {
            return remoteConfigs.Single(config => config.Key == key).Value;
        }

        private static void UseResourceWorkingDirectory()
        {
            if (!File.Exists("Configs/version_config.json") && Directory.Exists("Resources/Configs"))
                Directory.SetCurrentDirectory("Resources");
        }

        private static string ResourcePath(params string[] segments)
        {
            string[][] candidates =
            [
                segments,
                ["Resources", ..segments],
                ["..", "Resources", ..segments],
            ];

            foreach (string[] candidate in candidates)
            {
                string path = Path.Combine(candidate);
                if (File.Exists(path))
                    return path;
            }

            throw new FileNotFoundException($"Resource file not found: {Path.Combine(segments)}");
        }

        private static void AssertEqual<T>(T expected, T actual, string name)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidDataException($"{name}: expected '{expected}', got '{actual}'.");
        }

        private static void AssertEmptyList<T>(ICollection<T>? values, string name)
        {
            if (values is null)
                throw new InvalidDataException($"{name}: expected an empty list, got nil.");
            if (values.Count != 0)
                throw new InvalidDataException($"{name}: expected an empty list, got {values.Count} entries.");
        }

        class PropertyCompareResult
        {
            public string Name { get; private set; }
            public object OldValue { get; private set; }
            public object NewValue { get; private set; }

            public PropertyCompareResult(string name, object oldValue, object newValue)
            {
                Name = name;
                OldValue = oldValue;
                NewValue = newValue;
            }
        }

        class IgnorePropertyCompareAttribute : Attribute { }

        private static List<PropertyCompareResult> Compare<T>(T oldObject, T newObject, Type typecast = null)
        {
            PropertyInfo[] properties = null;
            if (typecast != null)
            {
                properties = typecast.GetProperties();
            }
            else
            {
                properties = typeof(T).GetProperties();
            }
            List<PropertyCompareResult> result = new List<PropertyCompareResult>();

            foreach (PropertyInfo pi in properties)
            {
                if (pi.CustomAttributes.Any(ca => ca.AttributeType == typeof(IgnorePropertyCompareAttribute)))
                {
                    continue;
                }

                object oldValue = pi.GetValue(oldObject), newValue = pi.GetValue(newObject);
                if (oldValue is null || newValue is null)
                {
                    if (!object.Equals(oldValue, newValue))
                        result.Add(new PropertyCompareResult(pi.Name, oldValue, newValue));
                    continue;
                }

                if (!object.Equals(oldValue, newValue))
                {
                    PropertyInfo[] propertyInfos = oldValue.GetType().GetProperties();
                    if (propertyInfos.Length > 1 && oldValue.GetType().IsClass && !oldValue.GetType().IsArray && !oldValue.GetType().IsGenericType)
                    {
                        result.AddRange(Compare(oldValue, newValue, oldValue.GetType()));
                    }
                    else
                    {
                        result.Add(new PropertyCompareResult(pi.Name, oldValue, newValue));
                    }
                }
            }

            return result;
        }

    }
}