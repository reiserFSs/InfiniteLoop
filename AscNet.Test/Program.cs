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
using AscNet.Table.V2.share.exhibition;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.grade;
using AscNet.Table.V2.share.character.skill;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.character.enhanceskill;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.client.draw;
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

                if (args.Contains("--login-account-compat-only"))
                {
                    ValidateLoginAccountCompatibility();
                    return;
                }
                if (args.Contains("--session-framing-compat-only"))
                {
                    ValidateSessionClientLoopFramingCompatibility();
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

                if (args.Contains("--mainline-luosaita-enter-compat-only"))
                {
                    ValidateMainLineLuosaitaEnterCompatibility();
                    return;
                }

                if (args.Contains("--mainline-treasure-reward-compat-only"))
                {
                    ValidateMainLineTreasureRewardCompatibility();
                    return;
                }

                if (args.Contains("--gather-awaken-reward-compat-only"))
                {
                    ValidateGatherAwakenRewardCompatibility();
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

                if (args.Contains("--character-skill-group-compat-only"))
                {
                    ValidateCharacterSkillGroupTableBackedCompatibility();
                    return;
                }
                if (args.Contains("--character-enhance-skill-compat-only"))
                {
                    ValidateCharacterEnhanceSkillTableBackedCompatibility();
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

                if (args.Contains("--prequel-reward-compat-only"))
                {
                    ValidatePrequelRewardCompatibility();
                    return;
                }

                if (args.Contains("--story-deploy-version-gap-compat-only"))
                {
                    ValidateStoryDeployVersionGapCompatibility();
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

                if (args.Contains("--jetavie-compat-only"))
                {
                    ValidateJetavieDaybreakTableCompatibility();
                    return;
                }

                if (args.Contains("--draw-compat-only"))
                {
                    ValidateDrawCompatibility();
                    return;
                }

                if (args.Contains("--item-use-compat-only"))
                {
                    ValidateItemUseCompatibility();
                    return;
                }

                if (args.Contains("--overclock-material-box-compat-only"))
                {
                    ValidateOverclockMaterialBoxCompatibility();
                    return;
                }

                if (args.Contains("--chat-compat-only"))
                {
                    ValidateChatCompatibility();
                    return;
                }

                if (args.Contains("--command-compat-only"))
                {
                    ValidateCommandCompatibility();
                    return;
                }

                if (args.Contains("--shop-compat-only"))
                {
                    ValidateShopCompatibility();
                    return;
                }

                if (args.Contains("--purchase-request-compat-only"))
                {
                    ValidatePurchaseRequestCompatibility();
                    return;
                }

                if (args.Contains("--missing-feature-compat-only"))
                {
                    ValidateMissingFeatureCompatibility();
                    return;
                }

                if (args.Contains("--current-client-notice-endpoints-only"))
                {
                    ValidateCurrentClientNoticeEndpoints().GetAwaiter().GetResult();
                    return;
                }

                if (args.Contains("--sync-read-game-notice-compat-only"))
                {
                    ValidateSyncReadGameNoticeRequestCompatibility();
                    return;
                }

                if (args.Contains("--lifetree-finish-process-compat-only"))
                {
                    ValidateLifeTreeFinishProcessRequestCompatibility();
                    return;
                }

                if (args.Contains("--steam-client-config-only"))
                {
                    ValidateSteamClientConfig();
                    return;
                }

                _ = JsonConvert.DeserializeObject<NotifyLogin>(File.ReadAllText(ResourcePath("Data", "NotifyLogin.json")))!;
                _ = JsonConvert.DeserializeObject<NotifyTaskData>(File.ReadAllText(ResourcePath("Data", "NotifyTaskData.json")))!;
                ValidateNotifyLoginCurrentClientCompatibilityShape();
                ValidateLoginAccountCompatibility();
                ValidateSessionClientLoopFramingCompatibility();
                ValidateStageBookmarkCompatibilityShape();
                ValidateMainLine2UpdateExhibitionChapterCompatibility();
                ValidateMainLineLuosaitaEnterCompatibility();
                ValidateMainLineTreasureRewardCompatibility();
                ValidateGatherAwakenRewardCompatibility();
                ValidateBossSingleLoginCompatibilityShape();
                ValidateCurrentClientGuideTableCompatibility();
                ValidatePlayerCostTimeUploadCompatibility();
                ValidateRecordPlayerPointCompatibility();
                ValidateBoardMutualClientPushCompatibility();
                ValidatePlayerGenderCompatibility();
                ValidateCharacterProgressionPersistenceCompatibility();
                ValidateCharacterSkillGroupTableBackedCompatibility();
                ValidateCharacterEnhanceSkillTableBackedCompatibility();
                ValidateExpLevelCompatibility();
                ValidateStoryCourseRewardCompatibility();
                ValidatePrequelRewardCompatibility();
                ValidateStoryDeployVersionGapCompatibility();
                ValidatePr2QualityCompatibility();
                ValidateInventoryEquipCompatibility();
                ValidateDrawCompatibility();
                ValidateItemUseCompatibility();
                ValidateChatCompatibility();
                ValidateCommandCompatibility();
                ValidateMissingFeatureCompatibility();
                ValidateShopCompatibility();
                ValidatePurchaseRequestCompatibility();
                ValidateCurrentClientNoticeFixtures();
                ValidateCurrentClientNoticeEndpoints().GetAwaiter().GetResult();
                ValidateLifeTreeFinishProcessRequestCompatibility();
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

        private static void ValidateSessionClientLoopFramingCompatibility()
        {
            Dictionary<string, RequestPacketHandlerDelegate> handlersSnapshot = new(PacketFactory.ReqHandlers);

            try
            {
                PacketFactory.LoadPacketHandlers();
                AssertSplitClientFrameTailPreservesSecondRequest();
                AssertExactReceiveBufferFillDoesNotDropFollowingRequest();
                AssertOversizedReceiveBufferFrameDoesNotDropFollowingRequest();
            }
            finally
            {
                PacketFactory.ReqHandlers.Clear();
                foreach (KeyValuePair<string, RequestPacketHandlerDelegate> handler in handlersSnapshot)
                    PacketFactory.ReqHandlers.Add(handler.Key, handler.Value);
            }
        }

        private static void AssertSplitClientFrameTailPreservesSecondRequest()
        {
            const long playerId = 88_007;
            const int firstPacketId = 91_001;
            const int secondPacketId = 91_002;

            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "session-framing-split-tail-compat-test");

            byte[] firstFrame = LoopbackSessionHarness.SerializeClientRequestFrame(nameof(HeartbeatRequest), firstPacketId, new HeartbeatRequest());
            byte[] secondFrame = LoopbackSessionHarness.SerializeClientRequestFrame(nameof(HeartbeatRequest), secondPacketId, new HeartbeatRequest());
            int secondFramePrefixLength = sizeof(int) + 1;
            if (secondFrame.Length <= secondFramePrefixLength)
                throw new InvalidDataException("Session.ClientLoop split-frame test setup error: HeartbeatRequest frame is too small to split after the payload begins.");

            byte[] firstWrite = GC.AllocateUninitializedArray<byte>(firstFrame.Length + secondFramePrefixLength);
            firstFrame.AsSpan().CopyTo(firstWrite);
            secondFrame.AsSpan(0, secondFramePrefixLength).CopyTo(firstWrite.AsSpan(firstFrame.Length));

            harness.WriteClientBytes(firstWrite);
            AssertHeartbeatResponse(harness, firstPacketId, "Session.ClientLoop split tail first HeartbeatRequest response before second tail");

            harness.WriteClientBytes(secondFrame.AsSpan(secondFramePrefixLength));
            AssertHeartbeatResponse(harness, secondPacketId, "Session.ClientLoop split tail second HeartbeatRequest response after tail");
        }

        private static void AssertExactReceiveBufferFillDoesNotDropFollowingRequest()
        {
            const long playerId = 88_008;
            const int receiveBufferLength = 1 << 16;
            const int fillingPacketId = 91_011;
            const int followingPacketId = 91_012;

            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "session-framing-exact-buffer-compat-test");

            byte[] fillingFrame = LoopbackSessionHarness.SerializeClientRequestFrameWithTotalLength(
                nameof(HeartbeatRequest),
                fillingPacketId,
                receiveBufferLength);
            byte[] followingFrame = LoopbackSessionHarness.SerializeClientRequestFrame(nameof(HeartbeatRequest), followingPacketId, new HeartbeatRequest());
            byte[] combinedWrite = GC.AllocateUninitializedArray<byte>(fillingFrame.Length + followingFrame.Length);
            fillingFrame.AsSpan().CopyTo(combinedWrite);
            followingFrame.AsSpan().CopyTo(combinedWrite.AsSpan(fillingFrame.Length));

            harness.WriteClientBytes(combinedWrite);
            AssertHeartbeatResponse(harness, fillingPacketId, "Session.ClientLoop exact receive-buffer fill HeartbeatRequest response");
            AssertHeartbeatResponse(harness, followingPacketId, "Session.ClientLoop request following exact receive-buffer fill response");
        }

        private static void AssertOversizedReceiveBufferFrameDoesNotDropFollowingRequest()
        {
            const long playerId = 88_009;
            const int historicalReceiveBufferLength = 1 << 16;
            const int oversizedFrameLength = historicalReceiveBufferLength + 1;
            const int oversizedPacketId = 91_021;
            const int followingPacketId = 91_022;

            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "session-framing-oversized-buffer-compat-test");

            byte[] oversizedFrame = LoopbackSessionHarness.SerializeClientRequestFrameWithTotalLength(
                nameof(HeartbeatRequest),
                oversizedPacketId,
                oversizedFrameLength);
            byte[] followingFrame = LoopbackSessionHarness.SerializeClientRequestFrame(nameof(HeartbeatRequest), followingPacketId, new HeartbeatRequest());
            byte[] combinedWrite = GC.AllocateUninitializedArray<byte>(oversizedFrame.Length + followingFrame.Length);
            oversizedFrame.AsSpan().CopyTo(combinedWrite);
            followingFrame.AsSpan().CopyTo(combinedWrite.AsSpan(oversizedFrame.Length));

            harness.WriteClientBytes(combinedWrite);
            AssertHeartbeatResponse(harness, oversizedPacketId, "Session.ClientLoop oversized receive-buffer HeartbeatRequest response");
            AssertHeartbeatResponse(harness, followingPacketId, "Session.ClientLoop request following oversized receive-buffer response");
        }

        private static void AssertHeartbeatResponse(LoopbackSessionHarness harness, int expectedPacketId, string name)
        {
            HeartbeatResponse response = ReadResponsePayload<HeartbeatResponse>(
                harness,
                expectedPacketId,
                nameof(HeartbeatResponse),
                name);
            if (response.UtcServerTime <= 0)
                throw new InvalidDataException($"{name}: expected a positive server UTC timestamp.");
        }

        private static void ValidateLoginAccountCompatibility()
        {
            ValidateLoginAccountNotifyLoginShape();
            ValidateSignInDailyRewardCompatibility();
            ValidateLoginStartupPushOrder();
            ValidateReconnectAckClientPushNoReplayStabilityCompatibility();
            ValidateClientVersionRequestCompatibility();
            ValidateLoginAccountNoticeFixtures();
            ValidateSyncReadGameNoticeRequestCompatibility();
            ValidateLoginHomeStateResponseCompatibility();
        }

        private static void ValidateLoginAccountNotifyLoginShape()
        {
            Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            MethodInfo buildNotifyLogin = RequiredMethod(
                accountModule,
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);

            const long playerId = 88_001;
            const long existingAccountStageId = 10010801;
            const long homeChatUnlockStageId = 10030201;
            long[] defaultPassedMainStoryStageIds =
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
            const long passedStarsMark = 7;
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.UseBackgroundId = 14_099_999;
            player.LastSignInTime = 0;
            player.PlayerData.Level = 7;
            long[] partialExistingCommunicationIds = [100, 102, 103, 104, 3, 2, 26];
            long[] expectedRetailCommunicationIds =
            [
                101, 102, 103, 104, 1, 105, 2, 3, 111, 106, 4, 5, 107, 108, 6, 7, 8, 9, 109,
                10, 11, 112, 12, 110, 14, 19, 25, 18, 20, 22, 24, 555, 21, 23, 599, 600,
                3108, 4108, 557, 558, 601, 602, 603, 604, 605, 556, 606, 607, 608, 609
            ];
            player.PlayerData.Communications = partialExistingCommunicationIds.ToList();
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            CharacterData[] ownedCharacters =
            [
                CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001),
                CreateLoginAccountCompatibilityCharacter(1031001, fashionId: 3031001)
            ];
            character.Characters.AddRange(ownedCharacters);
            AscNet.Common.Database.Stage stage = CreateLoginAccountCompatibilityStage(playerId);
            stage.AddStage(new StageDatum
            {
                StageId = existingAccountStageId,
                StarsMark = passedStarsMark,
                Passed = true,
                PassTimesToday = 0,
                PassTimesTotal = 1,
                BuyCount = 0,
                Score = 0,
                LastPassTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                RefreshTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BestRecordTime = 0,
                LastRecordTime = 0,
                BestCardIds = [1021001],
                LastCardIds = [1021001]
            });
            if (stage.Stages.ContainsKey(homeChatUnlockStageId))
                throw new InvalidDataException("Login account compatibility test setup error: input session stage unexpectedly already contains the home chat unlock stage.");
            int defaultMainStoryStagesBeforeLogin = defaultPassedMainStoryStageIds.Count(stage.Stages.ContainsKey);
            AssertEqual(0, defaultMainStoryStagesBeforeLogin, "Login account compatibility test setup default main-story awaken gate stages before login");

            NotifyLogin productionLogin;
            using (LoopbackSessionHarness harness = new(
                character,
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                "login-account-notify-login-compat-test"))
            {
                harness.Session.stage = stage;
                productionLogin = buildNotifyLogin.Invoke(null, [harness.Session]) as NotifyLogin
                    ?? throw new InvalidDataException("AccountModule.BuildNotifyLogin returned nil or a non-NotifyLogin payload.");
            }

            NotifyLogin roundTrip = MessagePackSerializer.Deserialize<NotifyLogin>(
                MessagePackSerializer.Serialize(productionLogin));

            if (roundTrip.SignInfos is null || roundTrip.SignInfos.Count == 0)
                throw new InvalidDataException("NotifyLogin SignInfos MessagePack round-trip: expected current sign-in schedule entries.");
            List<SignInfo> currentSignInfos = roundTrip.SignInfos.Where(signInfo => signInfo.Id == 1).ToList();
            AssertEqual(1, currentSignInfos.Count, "NotifyLogin SignInfos unsigned current daily reward entry count");
            AssertEqual(false, currentSignInfos[0].Got, "NotifyLogin SignInfos unsigned current daily reward Got");

            if (roundTrip.HaveBackgroundIds is null || roundTrip.HaveBackgroundIds.Count == 0)
                throw new InvalidDataException("NotifyLogin HaveBackgroundIds MessagePack round-trip: expected initialized owned background ids.");
            if (!roundTrip.HaveBackgroundIds.Contains(player.UseBackgroundId))
                throw new InvalidDataException($"NotifyLogin HaveBackgroundIds MessagePack round-trip: missing selected background id {player.UseBackgroundId}.");

            FunctionOpenTimeConfig? functionOpenTime = roundTrip.FunctionOpenTimeConfigList?
                .SingleOrDefault(config => config.FunctionId == 20000 && config.TimeId == 100);
            if (functionOpenTime is null)
                throw new InvalidDataException("NotifyLogin FunctionOpenTimeConfigList MessagePack round-trip: missing FunctionId 20000 / TimeId 100.");

            if (roundTrip.DlcPlayerData is null)
                throw new InvalidDataException("NotifyLogin DlcPlayerData MessagePack round-trip: expected initialized DLC player data.");
            if (roundTrip.DlcCharacterList is null)
                throw new InvalidDataException("NotifyLogin DlcCharacterList MessagePack round-trip: expected initialized DLC character list.");
            HashSet<uint> dlcCharacterIds = roundTrip.DlcCharacterList.Select(dlcCharacter => dlcCharacter.Id).ToHashSet();
            foreach (CharacterData ownedCharacter in ownedCharacters)
            {
                if (!dlcCharacterIds.Contains(ownedCharacter.Id))
                    throw new InvalidDataException($"NotifyLogin DlcCharacterList MessagePack round-trip: missing owned character {ownedCharacter.Id}.");
            }

            if (roundTrip.RedPointRecords is null)
                throw new InvalidDataException("NotifyLogin RedPointRecords MessagePack round-trip: expected initialized red-point records.");
            if (roundTrip.PlayerData.GuideData is null || roundTrip.PlayerData.GuideData.Count == 0)
                throw new InvalidDataException("NotifyLogin PlayerData.GuideData MessagePack round-trip: expected completed guide ids for unlocked home features.");
            if (roundTrip.FubenData?.StageData is not { } loginStageData)
                throw new InvalidDataException("NotifyLogin FubenData.StageData MessagePack round-trip: expected initialized account stages.");
            if (!loginStageData.TryGetValue(existingAccountStageId, out StageDatum? loginStage) || !loginStage.Passed)
                throw new InvalidDataException("NotifyLogin FubenData.StageData MessagePack round-trip: expected completed account stages for unlocked home features.");
            if (!loginStageData.TryGetValue(homeChatUnlockStageId, out StageDatum? homeChatUnlockStage))
                throw new InvalidDataException("NotifyLogin FubenData.StageData MessagePack round-trip: expected synthetic completed home chat unlock stage 10030201 when the input session stage does not contain it.");
            AssertEqual(true, homeChatUnlockStage.Passed, "NotifyLogin FubenData.StageData home chat unlock stage Passed");
            AssertEqual(passedStarsMark, homeChatUnlockStage.StarsMark, "NotifyLogin FubenData.StageData home chat unlock stage StarsMark");
            AssertDefaultPassedStageData(
                defaultPassedMainStoryStageIds,
                loginStageData,
                stage.Stages,
                "NotifyLogin FubenData.StageData default main-story awaken gate chain");
            AssertEqual(player.PlayerData.Level, roundTrip.PlayerData.Level, "NotifyLogin PlayerData.Level MessagePack round-trip");
            AssertEqual(true, roundTrip.IsSetFightCgEnable, "NotifyLogin IsSetFightCgEnable MessagePack round-trip");
            AssertCommunicationIdSet(
                expectedRetailCommunicationIds.Concat([100L, 26L]).ToArray(),
                roundTrip.PlayerData.Communications,
                "NotifyLogin PlayerData.Communications MessagePack round-trip partial existing profile retail defaults plus extras");
            AssertIntegerSetContainsAll(
                [801, 802, 803, 20000],
                roundTrip.PlayerData.Marks,
                "NotifyLogin PlayerData.Marks functional entrance gate defaults");
            if (roundTrip.FashionSuitList is null)
                throw new InvalidDataException("NotifyLogin FashionSuitList MessagePack round-trip: expected initialized list.");
            if (roundTrip.FashionColors is null)
                throw new InvalidDataException("NotifyLogin FashionColors MessagePack round-trip: expected initialized list.");

            static void AssertDefaultPassedStageData(
                IReadOnlyList<long> expectedStageIds,
                IReadOnlyDictionary<long, StageDatum> loginStageData,
                IReadOnlyDictionary<long, StageDatum> persistedStageData,
                string name)
            {
                int loginPassedStageCount = 0;
                int persistedPassedStageCount = 0;
                foreach (long expectedStageId in expectedStageIds)
                {
                    if (!loginStageData.TryGetValue(expectedStageId, out StageDatum? loginStage))
                        throw new InvalidDataException($"{name}: login payload missing default passed stage {expectedStageId}.");
                    AssertEqual(true, loginStage.Passed, $"{name} stage {expectedStageId} login payload Passed");
                    AssertEqual(7, loginStage.StarsMark, $"{name} stage {expectedStageId} login payload StarsMark");
                    loginPassedStageCount++;

                    if (!persistedStageData.TryGetValue(expectedStageId, out StageDatum? persistedStage))
                        throw new InvalidDataException($"{name}: persisted account stage document missing default passed stage {expectedStageId}.");
                    AssertEqual(true, persistedStage.Passed, $"{name} stage {expectedStageId} persisted Passed");
                    AssertEqual(7, persistedStage.StarsMark, $"{name} stage {expectedStageId} persisted StarsMark");
                    persistedPassedStageCount++;
                }

                AssertEqual(expectedStageIds.Count, loginPassedStageCount, $"{name} login payload migrated stage count");
                AssertEqual(expectedStageIds.Count, persistedPassedStageCount, $"{name} persisted migrated stage count");
            }

            static void AssertCommunicationIdSet(IReadOnlyList<long> expectedIds, IReadOnlyList<long> actualIds, string name)
            {
                HashSet<long> expectedDistinctIds = expectedIds.ToHashSet();
                HashSet<long> actualDistinctIds = actualIds.ToHashSet();

                AssertEqual(expectedDistinctIds.Count, actualDistinctIds.Count, $"{name} distinct count");
                AssertEqual(actualIds.Count, actualDistinctIds.Count, $"{name} duplicate count");
                foreach (long expectedId in expectedDistinctIds.Order())
                {
                    if (!actualDistinctIds.Contains(expectedId))
                        throw new InvalidDataException($"{name}: missing communication id {expectedId}.");
                }

                foreach (long actualId in actualDistinctIds.Order())
                {
                    if (!expectedDistinctIds.Contains(actualId))
                        throw new InvalidDataException($"{name}: unexpected communication id {actualId}.");
                }
            }
        }

        private static void ValidateSignInDailyRewardCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            const long playerId = 88_002;
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.LastSignInTime = 0;
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);

            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                player,
                inventory,
                "login-account-sign-in-compat-test");

            const int firstPacketId = 13_001;
            InvokeRegisteredRequestHandler(
                nameof(SignInRequest),
                harness.Session,
                firstPacketId,
                new SignInRequest { Id = 1 });
            SignInResponse firstResponse = (SignInResponse)ReadResponsePayload(
                harness,
                firstPacketId,
                nameof(SignInResponse),
                "SignInRequest first daily reward response",
                typeof(SignInResponse),
                maxPacketsToRead: 2);

            AssertEqual(0, firstResponse.Code, "SignInResponse first daily reward Code");
            AssertEqual(1, firstResponse.RewardGoodsList.Count, "SignInResponse first daily reward count");
            RewardGoods firstReward = firstResponse.RewardGoodsList[0];
            AssertEqual((int)RewardType.Item, firstReward.RewardType, "SignInResponse first daily reward RewardType");
            AssertEqual(AscNet.Common.Database.Inventory.Coin, firstReward.TemplateId, "SignInResponse first daily reward TemplateId");
            AssertEqual(10_000, firstReward.Count, "SignInResponse first daily reward Count");
            AssertEqual(50210, firstReward.Id, "SignInResponse first daily reward Id");
            AssertEqual(false, firstReward.IsGift, "SignInResponse first daily reward IsGift");
            AssertEqual(0, firstReward.RewardMulti, "SignInResponse first daily reward RewardMulti");
            if (player.LastSignInTime <= 0)
                throw new InvalidDataException("SignInRequest first daily reward: expected Player.LastSignInTime to be recorded.");

            Item coinAfterFirstSignIn = inventory.Items.Single(item => item.Id == AscNet.Common.Database.Inventory.Coin);
            AssertEqual(10_000L, coinAfterFirstSignIn.Count, "SignInRequest first daily reward inventory coin count");

            const int secondPacketId = 13_002;
            InvokeRegisteredRequestHandler(
                nameof(SignInRequest),
                harness.Session,
                secondPacketId,
                new SignInRequest { Id = 1 });
            SignInResponse secondResponse = (SignInResponse)ReadResponsePayload(
                harness,
                secondPacketId,
                nameof(SignInResponse),
                "SignInRequest duplicate same-day response",
                typeof(SignInResponse),
                maxPacketsToRead: 1);

            AssertEqual(0, secondResponse.Code, "SignInResponse duplicate same-day Code");
            AssertEmptyList(secondResponse.RewardGoodsList, "SignInResponse duplicate same-day RewardGoodsList");
            Item coinAfterSecondSignIn = inventory.Items.Single(item => item.Id == AscNet.Common.Database.Inventory.Coin);
            AssertEqual(10_000L, coinAfterSecondSignIn.Count, "SignInRequest duplicate same-day inventory coin count");
        }

        private static void ValidateLoginStartupPushOrder()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            MethodInfo doLogin = RequiredMethod(
                accountModule,
                "DoLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);

            const long playerId = 88_003;
            const long defaultChatBoardId = 25_000_001;
            const long existingCurrentChatBoardId = 25_000_007;
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PlayerData.LastLoginTime = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));

            const int validStartupItemId = AscNet.Common.Database.Inventory.Coin;
            const int invalidStartupItemId = 0;
            const long validStartupItemCount = 12_345;
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(
                playerId,
                [
                    new Item { Id = validStartupItemId, Count = validStartupItemCount },
                    new Item { Id = invalidStartupItemId, Count = 9_999 }
                ]);
            using LoopbackSessionHarness harness = new(
                character,
                player,
                inventory,
                "login-account-push-order-compat-test");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

            doLogin.Invoke(null, [harness.Session]);

            string[] retailCriticalStartupOrderThroughPassport =
            [
                nameof(NotifyLogin),
                nameof(NotifyPayInfo),
                nameof(NotifyMails),
                nameof(NotifyEquipChipGroupList),
                nameof(NotifyEquipChipAutoRecycleSite),
                nameof(NotifyEquipGuideData),
                nameof(NotifyArchiveLoginData),
                "NotifyLoginAwarenessInfo",
                nameof(NotifyChatLoginData),
                nameof(NotifySocialData),
                nameof(NotifyTaskData),
                nameof(NotifyActivenessStatus),
                nameof(NotifyNewPlayerTaskStatus),
                nameof(PurchaseDailyNotify),
                nameof(NotifyPurchaseRecommendConfig),
                "NotifyReviewConfig",
                "NotifyPassportData",
                "NotifyMentorData",
                "NotifyMentorChat",
                "NotifyGuildData",
                nameof(NotifyMails),
                "NotifyWheelchairManualActivityUpdate"
            ];
            string[] purchaseRelatedPushes =
            [
                nameof(PurchaseDailyNotify),
                nameof(NotifyPurchaseRecommendConfig)
            ];

            List<string> startupPushes = [];
            Dictionary<string, Packet.Push> startupPushesByName = new(StringComparer.Ordinal);
            const int maxStartupPushesUntilRetailTail = 192;
            for (int packetIndex = 0;
                packetIndex < maxStartupPushesUntilRetailTail && !startupPushes.Contains("NotifyWheelchairManualActivityUpdate");
                packetIndex++)
            {
                Packet.Push startupPush = ReadStartupPush(harness, $"AccountModule.DoLogin startup push {packetIndex + 1}");
                startupPushes.Add(startupPush.Name);
                startupPushesByName.TryAdd(startupPush.Name, startupPush);
            }

            if (!startupPushes.Contains("NotifyWheelchairManualActivityUpdate"))
                throw new InvalidDataException($"AccountModule.DoLogin startup pushes: expected retail startup tail NotifyWheelchairManualActivityUpdate; observed {DescribePushes(startupPushes)}.");

            AssertPushSubsequence(
                startupPushes,
                retailCriticalStartupOrderThroughPassport,
                "AccountModule.DoLogin retail-critical startup order");
            int guildDormPlayerDataIndex = RequiredPushIndex(
                startupPushes,
                "NotifyGuildDormPlayerData",
                0,
                "AccountModule.DoLogin chat-board predecessor");
            int chatBoardLoginDataIndex = RequiredPushIndex(
                startupPushes,
                nameof(NotifyChatBoardLoginData),
                0,
                "AccountModule.DoLogin NotifyChatBoardLoginData startup push");
            if (chatBoardLoginDataIndex <= guildDormPlayerDataIndex)
                throw new InvalidDataException($"AccountModule.DoLogin startup pushes: expected {nameof(NotifyChatBoardLoginData)} after NotifyGuildDormPlayerData; observed {DescribePushes(startupPushes)}.");

            NotifyLogin startupLogin = DeserializeStartupPush<NotifyLogin>(
                startupPushesByName,
                nameof(NotifyLogin),
                "AccountModule.DoLogin NotifyLogin startup payload");
            AssertClientItemList(
                startupLogin.ItemList,
                validStartupItemId,
                validStartupItemCount,
                invalidStartupItemId,
                "AccountModule.DoLogin NotifyLogin.ItemList");
            AssertIntegerList(
                [101, 102, 103, 104, 1, 105, 2, 3, 111, 106, 4, 5, 107, 108, 6, 7, 8, 9, 109,
                    10, 11, 112, 12, 110, 14, 19, 25, 18, 20, 22, 24, 555, 21, 23, 599, 600,
                    3108, 4108, 557, 558, 601, 602, 603, 604, 605, 556, 606, 607, 608, 609],
                startupLogin.PlayerData.Communications,
                "AccountModule.DoLogin NotifyLogin.PlayerData.Communications retail defaults");
            AssertIntegerSetContainsAll(
                [801, 802, 803, 20000],
                startupLogin.PlayerData.Marks,
                "AccountModule.DoLogin NotifyLogin.PlayerData.Marks functional entrance gate defaults");
            AssertIntegerList(
                [8001],
                startupLogin.PlayerData.ShieldFuncList.Cast<object>().Select(value => Convert.ToInt64(value)).ToArray(),
                "AccountModule.DoLogin NotifyLogin.PlayerData.ShieldFuncList retail defaults");

            FunctionOpenTimeConfig? startupFunctionOpenTime = startupLogin.FunctionOpenTimeConfigList?
                .SingleOrDefault(config => config.FunctionId == 20000 && config.TimeId == 100);
            if (startupFunctionOpenTime is null)
                throw new InvalidDataException("AccountModule.DoLogin NotifyLogin.FunctionOpenTimeConfigList: missing FunctionId 20000 / TimeId 100.");

            NotifyFunctionalEntranceData startupFunctionalEntranceData = DeserializeStartupPush<NotifyFunctionalEntranceData>(
                startupPushesByName,
                nameof(NotifyFunctionalEntranceData),
                "AccountModule.DoLogin NotifyFunctionalEntranceData startup payload");
            AssertIntegerDictionary(
                new Dictionary<int, int> { [20000] = 45 },
                startupFunctionalEntranceData.RedPointDatas,
                "AccountModule.DoLogin NotifyFunctionalEntranceData.RedPointDatas functional entrance redpoint");

            AscNet.GameServer.Handlers.NotifyWorldChat startupWorldChat = DeserializeStartupPush<AscNet.GameServer.Handlers.NotifyWorldChat>(
                startupPushesByName,
                nameof(AscNet.GameServer.Handlers.NotifyWorldChat),
                "AccountModule.DoLogin NotifyWorldChat startup payload");
            AssertWorldChatSeed(startupWorldChat, "AccountModule.DoLogin NotifyWorldChat startup seed");
            AssertStartupPayloadMapContainsKeys(
                startupPushesByName,
                nameof(NotifyChatBoardLoginData),
                "AccountModule.DoLogin NotifyChatBoardLoginData startup payload",
                nameof(NotifyChatBoardLoginData.CurrentChatBoardId),
                nameof(NotifyChatBoardLoginData.ChatBoards));
            NotifyChatBoardLoginData startupChatBoardLoginData = DeserializeStartupPush<NotifyChatBoardLoginData>(
                startupPushesByName,
                nameof(NotifyChatBoardLoginData),
                "AccountModule.DoLogin NotifyChatBoardLoginData startup payload");
            AssertChatBoardLoginData(
                startupChatBoardLoginData,
                defaultChatBoardId,
                defaultChatBoardId,
                "AccountModule.DoLogin sparse-player NotifyChatBoardLoginData startup payload");
            AssertEqual(defaultChatBoardId, player.PlayerData.CurrentChatBoardId, "AccountModule.DoLogin sparse-player CurrentChatBoardId default persistence");

            AssertStartupPayloadMapContainsKeys(
                startupPushesByName,
                "NotifyLoginAwarenessInfo",
                "AccountModule.DoLogin NotifyLoginAwarenessInfo startup payload",
                "AwarenessInfo");
            AssertStartupPayloadMapContainsKeys(
                startupPushesByName,
                "NotifyExperimentData",
                "AccountModule.DoLogin NotifyExperimentData startup payload",
                "FinishIds",
                "ExperimentInfos");
            AssertStartupPayloadMapContainsKeys(
                startupPushesByName,
                nameof(NotifyFubenBossSingleData),
                "AccountModule.DoLogin NotifyFubenBossSingleData startup payload",
                "FubenBossSingleData",
                "BossListDict");
            AssertStartupPayloadMapContainsKeys(
                startupPushesByName,
                "NotifyPassportData",
                "AccountModule.DoLogin NotifyPassportData startup payload",
                "ActivityId",
                "Level",
                "BaseInfo",
                "PassportInfos",
                "LastTimeBaseInfo",
                "IsGetSupplyReward",
                "IsActivateRegressionTask",
                "IsActivateNewbieTask");

            AssertForbiddenStartupPushesAbsent(
                startupPushes,
                [nameof(NotifyItemDataList), nameof(NotifyStageData), nameof(NotifyCharacterDataList), "NotifyPassportBaseInfo", "NotifyPassportAutoGetTaskReward", "NotifyGuildWarActivityData", "NotifyClientShieldFunction", "NotifyClientFunctionOpenConfig"],
                "AccountModule.DoLogin retail startup pushes");

            int purchasePredecessorIndex = RequiredPushIndex(startupPushes, nameof(NotifyNewPlayerTaskStatus), 0, "AccountModule.DoLogin purchase predecessor");
            foreach (string purchaseRelatedPush in purchaseRelatedPushes)
            {
                int purchaseRelatedPushIndex = RequiredPushIndex(startupPushes, purchaseRelatedPush, 0, "AccountModule.DoLogin purchase-related push");
                if (purchaseRelatedPushIndex <= purchasePredecessorIndex)
                    throw new InvalidDataException($"AccountModule.DoLogin startup pushes: expected {purchaseRelatedPush} only after {nameof(NotifyNewPlayerTaskStatus)}; observed {DescribePushes(startupPushes)}.");
            }

            List<string> remainingSynchronousPushes = DrainAvailablePushNames(
                harness,
                "AccountModule.DoLogin remaining synchronous startup packet");
            if (remainingSynchronousPushes.Count > 0)
                throw new InvalidDataException($"AccountModule.DoLogin startup pushes: expected no synchronous post-startup packets after NotifyWheelchairManualActivityUpdate; observed {DescribePushes(remainingSynchronousPushes)}.");

            Thread.Sleep(1_000);
            List<string> latePushes = DrainAvailablePushNames(
                harness,
                "AccountModule.DoLogin delayed post-startup packet");
            if (latePushes.Count > 0)
                throw new InvalidDataException($"AccountModule.DoLogin startup pushes: expected no delayed post-login replay after synchronous startup completed; observed {DescribePushes(latePushes)}.");

            AscNet.Common.Database.Player existingChatBoardPlayer = CreateDrawCompatibilityPlayer(playerId + 1);
            existingChatBoardPlayer.PlayerData.CurrentChatBoardId = existingCurrentChatBoardId;
            existingChatBoardPlayer.PlayerData.LastLoginTime = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            AscNet.Common.Database.Character existingChatBoardCharacter = CreateDrawCompatibilityCharacter(playerId + 1);
            existingChatBoardCharacter.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));
            using LoopbackSessionHarness existingChatBoardHarness = new(
                existingChatBoardCharacter,
                existingChatBoardPlayer,
                CreateDrawCompatibilityInventory(playerId + 1, []),
                "login-account-existing-chat-board-compat-test");
            existingChatBoardHarness.Session.stage = CreateLoginAccountCompatibilityStage(playerId + 1);

            doLogin.Invoke(null, [existingChatBoardHarness.Session]);
            Dictionary<string, Packet.Push> existingChatBoardStartupPushesByName = ReadStartupPushesByNameUntil(
                existingChatBoardHarness,
                nameof(NotifyChatBoardLoginData),
                maxStartupPushesUntilRetailTail,
                "AccountModule.DoLogin existing-chat-board startup pushes");
            NotifyChatBoardLoginData existingChatBoardLoginData = DeserializeStartupPush<NotifyChatBoardLoginData>(
                existingChatBoardStartupPushesByName,
                nameof(NotifyChatBoardLoginData),
                "AccountModule.DoLogin existing-chat-board NotifyChatBoardLoginData startup payload");
            AssertChatBoardLoginData(
                existingChatBoardLoginData,
                existingCurrentChatBoardId,
                defaultChatBoardId,
                "AccountModule.DoLogin existing CurrentChatBoardId NotifyChatBoardLoginData startup payload");
            AssertEqual(existingCurrentChatBoardId, existingChatBoardPlayer.PlayerData.CurrentChatBoardId, "AccountModule.DoLogin existing CurrentChatBoardId preservation");

            static List<string> DrainAvailablePushNames(LoopbackSessionHarness harness, string name)
            {
                List<string> pushNames = [];
                int packetIndex = 0;
                while (harness.TryReadAvailablePacket($"{name} {packetIndex + 1}", out Packet packet))
                {
                    AssertEqual(Packet.ContentType.Push, packet.Type, $"{name} {packetIndex + 1} packet type");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushNames.Add(push.Name);
                    packetIndex++;
                }

                return pushNames;
            }

            static void AssertPushSubsequence(IReadOnlyList<string> pushNames, IReadOnlyList<string> expectedOrder, string name)
            {
                int searchStart = 0;
                foreach (string expectedPushName in expectedOrder)
                {
                    int actualIndex = RequiredPushIndex(pushNames, expectedPushName, searchStart, name);
                    searchStart = actualIndex + 1;
                }
            }

            static int RequiredPushIndex(IReadOnlyList<string> pushNames, string expectedPushName, int searchStart, string name)
            {
                int index = FindPushIndex(pushNames, expectedPushName, searchStart);
                if (index < 0)
                    throw new InvalidDataException($"{name}: expected {expectedPushName} at or after startup push {searchStart + 1}; observed {DescribePushes(pushNames)}.");

                return index;
            }

            static int FindPushIndex(IReadOnlyList<string> pushNames, string expectedPushName, int searchStart)
            {
                for (int index = Math.Max(0, searchStart); index < pushNames.Count; index++)
                {
                    if (pushNames[index] == expectedPushName)
                        return index;
                }

                return -1;
            }

            static Dictionary<string, Packet.Push> ReadStartupPushesByNameUntil(
                LoopbackSessionHarness harness,
                string requiredPushName,
                int maxStartupPushes,
                string name)
            {
                List<string> pushNames = [];
                Dictionary<string, Packet.Push> pushesByName = new(StringComparer.Ordinal);
                for (int packetIndex = 0;
                    packetIndex < maxStartupPushes && !pushNames.Contains(requiredPushName);
                    packetIndex++)
                {
                    Packet.Push startupPush = ReadStartupPush(harness, $"{name} {packetIndex + 1}");
                    pushNames.Add(startupPush.Name);
                    pushesByName.TryAdd(startupPush.Name, startupPush);
                }

                if (!pushNames.Contains(requiredPushName))
                    throw new InvalidDataException($"{name}: expected {requiredPushName}; observed {DescribePushes(pushNames)}.");

                return pushesByName;
            }

            static Packet.Push ReadStartupPush(LoopbackSessionHarness harness, string name)
            {
                Packet packet = harness.ReadPacket(name);
                AssertEqual(Packet.ContentType.Push, packet.Type, $"{name} packet type");
                return MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
            }

            static TPayload DeserializeStartupPush<TPayload>(
                IReadOnlyDictionary<string, Packet.Push> startupPushesByName,
                string pushName,
                string name)
            {
                if (!startupPushesByName.TryGetValue(pushName, out Packet.Push? push))
                    throw new InvalidDataException($"{name}: expected {pushName} during login startup.");

                return MessagePackSerializer.Deserialize<TPayload>(push.Content);
            }

            static void AssertClientItemList(
                IReadOnlyList<Item>? items,
                int validItemId,
                long validItemCount,
                int invalidItemId,
                string name)
            {
                if (items is null)
                    throw new InvalidDataException($"{name}: expected initialized item list.");
                if (items.Any(item => item.Id == invalidItemId))
                    throw new InvalidDataException($"{name}: expected invalid item id {invalidItemId} to be filtered from the startup payload.");

                List<Item> validItems = items.Where(item => item.Id == validItemId).ToList();
                AssertEqual(1, validItems.Count, $"{name} valid item {validItemId} count");
                AssertEqual(validItemCount, validItems[0].Count, $"{name} valid item {validItemId} quantity");
            }

            static void AssertStartupPayloadMapContainsKeys(
                IReadOnlyDictionary<string, Packet.Push> startupPushesByName,
                string pushName,
                string name,
                params string[] requiredKeys)
            {
                if (!startupPushesByName.TryGetValue(pushName, out Packet.Push? push))
                    throw new InvalidDataException($"{name}: expected {pushName} during login startup.");

                HashSet<string> actualKeys = new(StringComparer.Ordinal);
                try
                {
                    MessagePackReader reader = new(new System.Buffers.ReadOnlySequence<byte>(push.Content));
                    int fieldCount = reader.ReadMapHeader();
                    for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                    {
                        string key = reader.ReadString()
                            ?? throw new InvalidDataException($"{name}: expected non-nil string map key at field {fieldIndex}.");
                        actualKeys.Add(key);
                        reader.Skip();
                    }
                }
                catch (Exception ex) when (ex is not InvalidDataException)
                {
                    throw new InvalidDataException($"{name}: expected a typed MessagePack map payload.", ex);
                }

                if (actualKeys.Count == 0)
                    throw new InvalidDataException($"{name}: expected a non-empty MessagePack map payload.");

                foreach (string requiredKey in requiredKeys)
                {
                    if (!actualKeys.Contains(requiredKey))
                        throw new InvalidDataException($"{name}: expected top-level key {requiredKey}; observed {DescribePushes(actualKeys.Order().ToArray())}.");
                }
            }

            static void AssertChatBoardLoginData(
                NotifyChatBoardLoginData payload,
                long expectedCurrentChatBoardId,
                long requiredBoardId,
                string name)
            {
                AssertEqual(expectedCurrentChatBoardId, payload.CurrentChatBoardId, $"{name} CurrentChatBoardId");
                if (payload.CurrentChatBoardId <= 0)
                    throw new InvalidDataException($"{name}: expected non-zero CurrentChatBoardId.");
                if (payload.ChatBoards.Count == 0)
                    throw new InvalidDataException($"{name}: expected non-empty ChatBoards.");
                if (!payload.ChatBoards.Any(chatBoard => chatBoard.Id == payload.CurrentChatBoardId))
                    throw new InvalidDataException($"{name}: expected ChatBoards to contain current board {payload.CurrentChatBoardId}; observed {DescribePushes(payload.ChatBoards.Select(chatBoard => chatBoard.Id.ToString()).ToArray())}.");
                if (!payload.ChatBoards.Any(chatBoard => chatBoard.Id == requiredBoardId))
                    throw new InvalidDataException($"{name}: expected ChatBoards to contain board {requiredBoardId}; observed {DescribePushes(payload.ChatBoards.Select(chatBoard => chatBoard.Id.ToString()).ToArray())}.");

                for (int chatBoardIndex = 0; chatBoardIndex < payload.ChatBoards.Count; chatBoardIndex++)
                {
                    NotifyChatBoardLoginData.NotifyChatBoardLoginDataChatBoard chatBoard = payload.ChatBoards[chatBoardIndex];
                    if (chatBoard.GetTime == 0)
                        throw new InvalidDataException($"{name}: expected ChatBoards[{chatBoardIndex}] Id {chatBoard.Id} to have non-zero GetTime.");
                    if (chatBoard.EndTime < 0)
                        throw new InvalidDataException($"{name}: expected ChatBoards[{chatBoardIndex}] Id {chatBoard.Id} EndTime to be >= 0.");
                }
            }

            static void AssertForbiddenStartupPushesAbsent(
                IReadOnlyList<string> pushNames,
                IReadOnlyList<string> forbiddenPushNames,
                string name)
            {
                foreach (string forbiddenPushName in forbiddenPushNames)
                {
                    if (pushNames.Contains(forbiddenPushName))
                        throw new InvalidDataException($"{name}: expected unsupported startup push {forbiddenPushName} not to be emitted; observed {DescribePushes(pushNames)}.");
                }
            }

            static string DescribePushes(IReadOnlyList<string> pushNames)
            {
                return pushNames.Count == 0
                    ? "<none>"
                    : string.Join(" -> ", pushNames);
            }
        }

        private static void ValidateReconnectAckClientPushNoReplayStabilityCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();

            const long playerId = 88_005;
            const string reconnectToken = "login-account-reconnect-token";
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.Token = reconnectToken;
            player.PlayerData.LastLoginTime = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds();
            player.PlayerData.NewPlayerTaskActiveDay = 9;
            player.GatherRewards = [5, 6];
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(
                playerId,
                [new Item { Id = AscNet.Common.Database.Inventory.Coin, Count = 12_345 }]);

            using LoopbackSessionHarness harness = new(
                character,
                player,
                inventory,
                "login-account-reconnect-ack-compat-test");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

            long lastLoginTimeBeforeReconnectAck = player.PlayerData.LastLoginTime;
            int newPlayerTaskActiveDayBeforeReconnectAck = player.PlayerData.NewPlayerTaskActiveDay;
            int[] gatherRewardsBeforeReconnectAck = player.GatherRewards.ToArray();

            const int reconnectLastMsgSeqNo = 123;
            const int reconnectPacketId = 13_008;
            InvokeRegisteredRequestHandler(
                nameof(ReconnectRequest),
                harness.Session,
                reconnectPacketId,
                new ReconnectRequest
                {
                    Token = reconnectToken,
                    PlayerId = (uint)playerId,
                    LastMsgSeqNo = reconnectLastMsgSeqNo
                });
            ReconnectResponse reconnectResponse = ReadResponsePayload<ReconnectResponse>(
                harness,
                reconnectPacketId,
                nameof(ReconnectResponse),
                "ReconnectRequest valid token response");
            AssertEqual(0, reconnectResponse.Code, "ReconnectResponse valid token Code");
            AssertEqual(reconnectToken, reconnectResponse.ReconnectToken, "ReconnectResponse valid token ReconnectToken");
            AssertEqual(reconnectLastMsgSeqNo, reconnectResponse.RequestNo, "ReconnectResponse valid token RequestNo");

            harness.SendClientPush("ReconnectAck", []);

            if (harness.TryReadAvailablePacket("ReconnectAck client push follow-up packet", out Packet followUpPacket)
                && followUpPacket.Type == Packet.ContentType.Push)
            {
                Packet.Push followUpPush = MessagePackSerializer.Deserialize<Packet.Push>(followUpPacket.Content);
                string[] forbiddenStartupReplayPushes =
                [
                    nameof(NotifyLogin),
                    nameof(NotifyPayInfo),
                    "NotifyPassportBaseInfo",
                    nameof(NotifyItemDataList),
                    nameof(NotifyStageData),
                    nameof(NotifyCharacterDataList)
                ];

                if (forbiddenStartupReplayPushes.Contains(followUpPush.Name))
                    throw new InvalidDataException($"ReconnectAck client push follow-up packet: expected no startup replay push, observed {followUpPush.Name}.");
            }

            AssertEqual(lastLoginTimeBeforeReconnectAck, player.PlayerData.LastLoginTime, "ReconnectAck client push PlayerData.LastLoginTime stability");
            AssertEqual(newPlayerTaskActiveDayBeforeReconnectAck, player.PlayerData.NewPlayerTaskActiveDay, "ReconnectAck client push PlayerData.NewPlayerTaskActiveDay stability");
            AssertIntegerList(
                gatherRewardsBeforeReconnectAck.Select(rewardId => (long)rewardId).ToArray(),
                player.GatherRewards.Select(rewardId => (long)rewardId).ToArray(),
                "ReconnectAck client push gather rewards stability");
        }

        private static void ValidateClientVersionRequestCompatibility()
        {
            const long playerId = 88_006;
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "login-account-client-version-compat-test");

            const int clientVersionPacketId = 13_009;
            InvokeRegisteredRequestHandler(
                nameof(ClientVersionRequest),
                harness.Session,
                clientVersionPacketId,
                new ClientVersionRequest { Version = "4.5.0" });
            ClientVersionResponse response = ReadResponsePayload<ClientVersionResponse>(
                harness,
                clientVersionPacketId,
                nameof(ClientVersionResponse),
                "ClientVersionRequest response");

            AssertEqual(0, response.Code, "ClientVersionResponse Code");
            if (string.IsNullOrWhiteSpace(response.Version))
                throw new InvalidDataException("ClientVersionResponse Version: expected a non-empty version string.");
            AssertEqual(false, response.KickOut, "ClientVersionResponse KickOut");
        }

        private static void ValidateLoginAccountNoticeFixtures()
        {
            AssertNoticeFixtureHasNonEmptyContent("SecondMenuNotice.json", "SecondMenuNotice");
            AssertNoticeFixtureHasEmptyContent("PopUpPicNotice.json", "PopUpPicNotice");

            static void AssertNoticeFixtureHasNonEmptyContent(string fileName, string name)
            {
                JArray content = RequiredNoticeContent(fileName, name);
                if (content.Count == 0)
                    throw new InvalidDataException($"{name} Content: expected at least one notice entry.");
            }

            static void AssertNoticeFixtureHasEmptyContent(string fileName, string name)
            {
                JArray content = RequiredNoticeContent(fileName, name);
                if (content.Count != 0)
                    throw new InvalidDataException($"{name} Content: expected an initialized empty array, got {content.Count} notice entries.");
            }

            static JArray RequiredNoticeContent(string fileName, string name)
            {
                string path = ResourcePath("Configs", "Notices", "4.5.0", fileName);
                JObject notice = JObject.Parse(File.ReadAllText(path));
                return notice.Value<JArray>("Content")
                    ?? throw new InvalidDataException($"{name} Content: expected a JSON array.");
            }
        }

        private static void ValidateSyncReadGameNoticeRequestCompatibility()
        {
            const string requestName = "SyncReadGameNoticeRequest";
            const string responseName = "SyncReadGameNoticeResponse";

            MethodInfo handlerMethod = GetRegisteredRequestHandlerMethod(requestName);
            AssertEqual("SyncReadGameNoticeRequestHandler", handlerMethod.Name, $"{requestName} registered handler method");

            RequestPacketHandlerDelegate handler = GetRegisteredRequestHandler(requestName);
            const long playerId = 88_007;
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "login-account-sync-read-game-notice-compat-test");

            const string noticeId = "6a1e0fd0f1b4a13fd8bf4900";
            const long modifyTime = 1_780_355_024;
            const long endTime = 1_783_580_100;
            SyncReadGameNoticeRequest request = new()
            {
                GameNoticeInfos =
                [
                    new RedPointGameNoticeInfo
                    {
                        NoticeId = noticeId,
                        ModifyTime = modifyTime,
                        EndTime = endTime
                    }
                ]
            };
            SyncReadGameNoticeRequest requestRoundTrip = MessagePackSerializer.Deserialize<SyncReadGameNoticeRequest>(
                MessagePackSerializer.Serialize(request));
            AssertEqual(1, requestRoundTrip.GameNoticeInfos.Count, $"{requestName} GameNoticeInfos MessagePack round-trip count");
            RedPointGameNoticeInfo noticeInfoRoundTrip = requestRoundTrip.GameNoticeInfos[0];
            AssertEqual(noticeId, noticeInfoRoundTrip.NoticeId, $"{requestName} GameNoticeInfos[0].NoticeId MessagePack round-trip");
            AssertEqual(modifyTime, noticeInfoRoundTrip.ModifyTime, $"{requestName} GameNoticeInfos[0].ModifyTime MessagePack round-trip");
            AssertEqual(endTime, noticeInfoRoundTrip.EndTime, $"{requestName} GameNoticeInfos[0].EndTime MessagePack round-trip");

            const int packetId = 13_010;
            Packet.Request packet = new()
            {
                Id = packetId,
                Name = requestName,
                Content = MessagePackSerializer.Serialize(requestRoundTrip)
            };

            try
            {
                handler.Invoke(harness.Session, packet);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"{requestName}: registered handler invocation failed for verified live MessagePack payload shape.", exception);
            }

            Type responseType = RequiredAscNetGameServerType($"AscNet.GameServer.Handlers.{responseName}");
            object response = ReadResponsePayload(
                harness,
                packetId,
                responseName,
                $"{requestName} response",
                responseType);
            AssertEqual(0, GetRequiredIntegerMember(response, "Code"), $"{responseName} Code");

            AssertSingleGameNoticeInfo(
                harness.Session.player.RedPointRecords?.GameNoticeInfos,
                noticeId,
                modifyTime,
                endTime,
                $"{requestName} persisted Player.RedPointRecords.GameNoticeInfos");

            Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            MethodInfo buildNotifyLogin = RequiredMethod(
                accountModule,
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            NotifyLogin notifyLogin = buildNotifyLogin.Invoke(null, [harness.Session]) as NotifyLogin
                ?? throw new InvalidDataException("AccountModule.BuildNotifyLogin returned nil or a non-NotifyLogin payload.");
            NotifyLogin notifyLoginRoundTrip = MessagePackSerializer.Deserialize<NotifyLogin>(
                MessagePackSerializer.Serialize(notifyLogin));
            AssertSingleGameNoticeInfo(
                notifyLoginRoundTrip.RedPointRecords?.GameNoticeInfos,
                noticeId,
                modifyTime,
                endTime,
                $"{requestName} NotifyLogin RedPointRecords.GameNoticeInfos MessagePack round-trip");

            static void AssertSingleGameNoticeInfo(
                IReadOnlyList<RedPointGameNoticeInfo>? actual,
                string expectedNoticeId,
                long expectedModifyTime,
                long expectedEndTime,
                string name)
            {
                if (actual is null)
                    throw new InvalidDataException($"{name}: expected persisted live notice info, got nil.");
                AssertEqual(1, actual.Count, $"{name} count");
                RedPointGameNoticeInfo actualNoticeInfo = actual[0];
                AssertEqual(expectedNoticeId, actualNoticeInfo.NoticeId, $"{name}[0].NoticeId");
                AssertEqual(expectedModifyTime, actualNoticeInfo.ModifyTime, $"{name}[0].ModifyTime");
                AssertEqual(expectedEndTime, actualNoticeInfo.EndTime, $"{name}[0].EndTime");
            }
        }

        private static void ValidateLifeTreeFinishProcessRequestCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            const string requestName = nameof(LifeTreeFinishProcessRequest);
            const string responseName = nameof(LifeTreeFinishProcessResponse);

            MethodInfo handlerMethod = GetRegisteredRequestHandlerMethod(requestName);
            AssertEqual("LifeTreeFinishProcessRequestHandler", handlerMethod.Name, $"{requestName} registered handler method");

            RequestPacketHandlerDelegate handler = GetRegisteredRequestHandler(requestName);
            Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            MethodInfo doLogin = RequiredMethod(
                accountModule,
                "DoLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);

            const long playerId = 88_008;
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PlayerData.LastLoginTime = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));
            using LoopbackSessionHarness harness = new(
                character,
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                "lifetree-finish-process-compat-test");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

            LifeTreeFinishProcessRequest processOneRequest = new()
            {
                Process = 1
            };
            byte[] processOneRequestPayload = MessagePackSerializer.Serialize(processOneRequest);
            AssertEqual("81A750726F6365737301", Convert.ToHexString(processOneRequestPayload), $"{requestName} Process=1 verified live MessagePack payload");
            LifeTreeFinishProcessRequest processOneRequestRoundTrip = MessagePackSerializer.Deserialize<LifeTreeFinishProcessRequest>(processOneRequestPayload);
            AssertEqual(1, processOneRequestRoundTrip.Process, $"{requestName} Process=1 MessagePack round-trip");

            const int processOnePacketId = 13_011;
            Packet.Request processOnePacket = new()
            {
                Id = processOnePacketId,
                Name = requestName,
                Content = processOneRequestPayload
            };

            try
            {
                handler.Invoke(harness.Session, processOnePacket);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"{requestName}: registered handler invocation failed for verified Process=1 MessagePack payload.", exception);
            }

            AssertCompletedLifeTreeData(player.LifeTreeData, $"{requestName} Process=1 persisted Player.LifeTreeData");

            NotifyLifeTreeData finishPush = ReadPushPayload<NotifyLifeTreeData>(
                harness,
                nameof(NotifyLifeTreeData),
                $"{requestName} Process=1 NotifyLifeTreeData push");
            AssertCompletedLifeTreeData(finishPush, $"{requestName} Process=1 NotifyLifeTreeData push");

            LifeTreeFinishProcessResponse processOneResponse = ReadResponsePayload<LifeTreeFinishProcessResponse>(
                harness,
                processOnePacketId,
                responseName,
                $"{requestName} Process=1 response");
            AssertEqual(0, processOneResponse.Code, $"{responseName} Process=1 Code");

            MethodInfo processTwoHandlerMethod = GetRegisteredRequestHandlerMethod(requestName);
            AssertEqual("LifeTreeFinishProcessRequestHandler", processTwoHandlerMethod.Name, $"{requestName} handler remains registered after Process=1");

            LifeTreeFinishProcessRequest processTwoRequest = new()
            {
                Process = 2
            };
            byte[] processTwoRequestPayload = MessagePackSerializer.Serialize(processTwoRequest);
            AssertEqual("81A750726F6365737302", Convert.ToHexString(processTwoRequestPayload), $"{requestName} Process=2 verified live MessagePack payload");
            LifeTreeFinishProcessRequest processTwoRequestRoundTrip = MessagePackSerializer.Deserialize<LifeTreeFinishProcessRequest>(processTwoRequestPayload);
            AssertEqual(2, processTwoRequestRoundTrip.Process, $"{requestName} Process=2 MessagePack round-trip");
            string completedLifeTreeDataSnapshot = Convert.ToHexString(MessagePackSerializer.Serialize(player.LifeTreeData));

            const int processTwoPacketId = 13_012;
            Packet.Request processTwoPacket = new()
            {
                Id = processTwoPacketId,
                Name = requestName,
                Content = processTwoRequestPayload
            };

            try
            {
                handler.Invoke(harness.Session, processTwoPacket);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"{requestName}: registered handler invocation failed for verified Process=2 MessagePack payload.", exception);
            }

            AssertEqual(completedLifeTreeDataSnapshot, Convert.ToHexString(MessagePackSerializer.Serialize(player.LifeTreeData)), $"{requestName} Process=2 persisted Player.LifeTreeData unchanged");
            AssertCompletedLifeTreeData(player.LifeTreeData, $"{requestName} Process=2 persisted Player.LifeTreeData");

            NotifyLifeTreeData processTwoPush = ReadPushPayload<NotifyLifeTreeData>(
                harness,
                nameof(NotifyLifeTreeData),
                $"{requestName} Process=2 NotifyLifeTreeData push");
            AssertCompletedLifeTreeData(processTwoPush, $"{requestName} Process=2 NotifyLifeTreeData push");

            LifeTreeFinishProcessResponse processTwoResponse = ReadResponsePayload<LifeTreeFinishProcessResponse>(
                harness,
                processTwoPacketId,
                responseName,
                $"{requestName} Process=2 response");
            AssertEqual(0, processTwoResponse.Code, $"{responseName} Process=2 Code");
            doLogin.Invoke(null, [harness.Session]);
            NotifyLifeTreeData startupLifeTreeData = ReadStartupLifeTreePush(
                harness,
                maxStartupPushes: 192,
                "AccountModule.DoLogin LifeTree startup pushes");
            AssertCompletedLifeTreeData(startupLifeTreeData, "AccountModule.DoLogin NotifyLifeTreeData persisted startup payload");

            static NotifyLifeTreeData ReadStartupLifeTreePush(
                LoopbackSessionHarness harness,
                int maxStartupPushes,
                string name)
            {
                List<string> pushNames = [];
                for (int packetIndex = 0; packetIndex < maxStartupPushes; packetIndex++)
                {
                    Packet packet = harness.ReadPacket($"{name} {packetIndex + 1}");
                    AssertEqual(Packet.ContentType.Push, packet.Type, $"{name} {packetIndex + 1} packet type");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushNames.Add(push.Name);
                    if (push.Name == nameof(NotifyLifeTreeData))
                        return MessagePackSerializer.Deserialize<NotifyLifeTreeData>(push.Content);
                }

                string observedPushes = pushNames.Count == 0 ? "<none>" : string.Join(", ", pushNames);
                throw new InvalidDataException($"{name}: expected {nameof(NotifyLifeTreeData)} within {maxStartupPushes} startup pushes; observed {observedPushes}.");
            }

            static void AssertCompletedLifeTreeData(NotifyLifeTreeData? data, string name)
            {
                if (data is null)
                    throw new InvalidDataException($"{name}: expected completed LifeTree data, got nil.");

                AssertEqual(true, data.IsFinishGuide, $"{name}.IsFinishGuide");
                AssertEqual(true, data.IsFinishLifeTreePv, $"{name}.IsFinishLifeTreePv");
                AssertIntegerSetContainsAll(
                    [61, 58, 57],
                    data.FinishedChapters.Select(chapterId => (long)chapterId).ToArray(),
                    $"{name}.FinishedChapters");
                AssertUnlockedCharacter(1031005, data.UnlockCharacterData, name);
                AssertUnlockedCharacter(1021007, data.UnlockCharacterData, name);
            }

            static void AssertUnlockedCharacter(
                int characterId,
                IReadOnlyDictionary<int, LifeTreeUnlockCharacterData>? unlockCharacterData,
                string name)
            {
                if (unlockCharacterData is null)
                    throw new InvalidDataException($"{name}.UnlockCharacterData: expected completed unlock character map, got nil.");
                if (!unlockCharacterData.TryGetValue(characterId, out LifeTreeUnlockCharacterData? characterData))
                    throw new InvalidDataException($"{name}.UnlockCharacterData: missing character {characterId}.");

                AssertEqual(characterId, characterData.Id, $"{name}.UnlockCharacterData[{characterId}].Id");
                AssertEqual(1, characterData.UnlockStatus, $"{name}.UnlockCharacterData[{characterId}].UnlockStatus");
            }
        }

        private static void ValidateLoginHomeStateResponseCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            const long playerId = 88_004;
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "login-account-home-state-compat-test");

            const int purchasePacketId = 13_003;
            GetPurchaseListRequest purchaseRequest = new()
            {
                UiTypeList = [11]
            };
            GetPurchaseListRequest purchaseRequestRoundTrip = MessagePackSerializer.Deserialize<GetPurchaseListRequest>(
                MessagePackSerializer.Serialize(purchaseRequest));
            AssertIntegerList([11], purchaseRequestRoundTrip.UiTypeList.Select(uiType => (long)uiType).ToArray(), "GetPurchaseListRequest UiTypeList MessagePack round-trip");
            InvokeRegisteredRequestHandler(nameof(GetPurchaseListRequest), harness.Session, purchasePacketId, purchaseRequestRoundTrip);
            GetPurchaseListResponse purchaseResponse = ReadResponsePayload<GetPurchaseListResponse>(
                harness,
                purchasePacketId,
                nameof(GetPurchaseListResponse),
                "GetPurchaseListRequest UiType 11 response");
            AssertEqual(0, purchaseResponse.Code, "GetPurchaseListResponse UiType 11 Code");
            AssertEqual(true, purchaseResponse.PurchaseInfoList.Count > 0, "GetPurchaseListResponse UiType 11 PurchaseInfoList non-empty");
            if (purchaseResponse.PurchaseComboInfoList is null)
                throw new InvalidDataException("GetPurchaseListResponse UiType 11 PurchaseComboInfoList: expected initialized list.");

            GetPurchaseListResponse purchaseRoundTrip = MessagePackSerializer.Deserialize<GetPurchaseListResponse>(
                MessagePackSerializer.Serialize(purchaseResponse));
            System.Collections.IDictionary firstPurchase = RequiredDynamicMap(
                purchaseRoundTrip.PurchaseInfoList.FirstOrDefault(),
                "GetPurchaseListResponse UiType 11 PurchaseInfoList[0] MessagePack round-trip");
            AssertEqual(11, RequiredDynamicInteger(firstPurchase, "UiType", "GetPurchaseListResponse UiType 11 PurchaseInfoList[0] MessagePack round-trip"), "GetPurchaseListResponse UiType 11 PurchaseInfoList[0] UiType");
            List<object> rewardGoodsList = RequiredDynamicObjectList(firstPurchase, "RewardGoodsList", "GetPurchaseListResponse UiType 11 PurchaseInfoList[0] RewardGoodsList MessagePack round-trip");
            AssertEqual(true, rewardGoodsList.Count > 0, "GetPurchaseListResponse UiType 11 PurchaseInfoList[0] RewardGoodsList non-empty");
            System.Collections.IDictionary firstRewardGoods = RequiredDynamicMap(
                rewardGoodsList[0],
                "GetPurchaseListResponse UiType 11 PurchaseInfoList[0] RewardGoodsList[0] MessagePack round-trip");
            AssertEqual(false, RequiredDynamicBoolean(firstRewardGoods, "IsGift", "GetPurchaseListResponse UiType 11 PurchaseInfoList[0] RewardGoodsList[0] MessagePack round-trip"), "GetPurchaseListResponse UiType 11 RewardGoods IsGift retail field");
            AssertEqual(0, RequiredDynamicInteger(firstRewardGoods, "RewardMulti", "GetPurchaseListResponse UiType 11 PurchaseInfoList[0] RewardGoodsList[0] MessagePack round-trip"), "GetPurchaseListResponse UiType 11 RewardGoods RewardMulti retail field");
            AssertEqual(50024560, RequiredDynamicInteger(firstRewardGoods, "Id", "GetPurchaseListResponse UiType 11 PurchaseInfoList[0] RewardGoodsList[0] MessagePack round-trip"), "GetPurchaseListResponse UiType 11 RewardGoods Id retail field");

            AssertPurchaseListForUiTypes(
                harness,
                13_103,
                [5, 6, 2],
                [2, 5, 6],
                [2],
                [7, 8, 11, 15],
                "5/6/2");
            AssertPurchaseListForUiTypes(
                harness,
                13_104,
                [5, 6],
                [5, 6],
                [],
                [2, 7, 8, 11, 15],
                "5/6");
            AssertPurchaseListForUiTypes(
                harness,
                13_105,
                [5, 2],
                [2, 5],
                [2],
                [6, 7, 8, 11, 15],
                "5/2");

            static void AssertPurchaseListForUiTypes(
                LoopbackSessionHarness harness,
                int packetId,
                int[] requestedUiTypes,
                int[] allowedUiTypes,
                int[] requiredUiTypes,
                int[] excludedUiTypes,
                string uiTypeName)
            {
                GetPurchaseListRequest request = new()
                {
                    UiTypeList = requestedUiTypes.ToList()
                };
                GetPurchaseListRequest requestRoundTrip = MessagePackSerializer.Deserialize<GetPurchaseListRequest>(
                    MessagePackSerializer.Serialize(request));
                AssertIntegerList(
                    requestedUiTypes.Select(uiType => (long)uiType).ToArray(),
                    requestRoundTrip.UiTypeList.Select(uiType => (long)uiType).ToArray(),
                    $"GetPurchaseListRequest UiTypeList {uiTypeName} MessagePack round-trip");
                InvokeRegisteredRequestHandler(nameof(GetPurchaseListRequest), harness.Session, packetId, requestRoundTrip);
                GetPurchaseListResponse response = ReadResponsePayload<GetPurchaseListResponse>(
                    harness,
                    packetId,
                    nameof(GetPurchaseListResponse),
                    $"GetPurchaseListRequest UiTypes {uiTypeName} response");
                AssertEqual(0, response.Code, $"GetPurchaseListResponse UiTypes {uiTypeName} Code");
                AssertEqual(true, response.PurchaseInfoList.Count > 0, $"GetPurchaseListResponse UiTypes {uiTypeName} PurchaseInfoList non-empty");

                GetPurchaseListResponse roundTrip = MessagePackSerializer.Deserialize<GetPurchaseListResponse>(
                    MessagePackSerializer.Serialize(response));
                HashSet<int> allowedPurchaseUiTypes = allowedUiTypes.ToHashSet();
                HashSet<int> actualPurchaseUiTypes = [];
                for (int purchaseIndex = 0; purchaseIndex < roundTrip.PurchaseInfoList.Count; purchaseIndex++)
                {
                    System.Collections.IDictionary purchaseInfo = RequiredDynamicMap(
                        (object?)roundTrip.PurchaseInfoList[purchaseIndex],
                        $"GetPurchaseListResponse UiTypes {uiTypeName} PurchaseInfoList[{purchaseIndex}] MessagePack round-trip");
                    int uiType = RequiredDynamicInteger(purchaseInfo, "UiType", $"GetPurchaseListResponse UiTypes {uiTypeName} PurchaseInfoList[{purchaseIndex}] MessagePack round-trip");
                    actualPurchaseUiTypes.Add(uiType);
                    AssertEqual(true, allowedPurchaseUiTypes.Contains(uiType), $"GetPurchaseListResponse UiTypes {uiTypeName} PurchaseInfoList[{purchaseIndex}] requested UiType");
                }

                foreach (int requiredUiType in requiredUiTypes)
                    AssertEqual(true, actualPurchaseUiTypes.Contains(requiredUiType), $"GetPurchaseListResponse UiTypes {uiTypeName} includes UiType {requiredUiType}");
                foreach (int excludedUiType in excludedUiTypes)
                    AssertEqual(false, actualPurchaseUiTypes.Contains(excludedUiType), $"GetPurchaseListResponse UiTypes {uiTypeName} excludes UiType {excludedUiType}");
            }

            const int shopBaseInfoPacketId = 13_004;
            InvokeRegisteredRequestHandler(nameof(GetShopBaseInfoRequest), harness.Session, shopBaseInfoPacketId, new GetShopBaseInfoRequest());
            GetShopBaseInfoResponse shopBaseInfoResponse = ReadResponsePayload<GetShopBaseInfoResponse>(
                harness,
                shopBaseInfoPacketId,
                nameof(GetShopBaseInfoResponse),
                "GetShopBaseInfoRequest response");
            AssertEqual(true, shopBaseInfoResponse.ShopBaseInfoList.Count > 0, "GetShopBaseInfoResponse ShopBaseInfoList non-empty");

            const int lottoInfoPacketId = 13_005;
            InvokeRegisteredRequestHandler(nameof(LottoInfoRequest), harness.Session, lottoInfoPacketId, new LottoInfoRequest());
            LottoInfoResponse lottoInfoResponse = ReadResponsePayload<LottoInfoResponse>(
                harness,
                lottoInfoPacketId,
                nameof(LottoInfoResponse),
                "LottoInfoRequest response");
            AssertEqual(0, lottoInfoResponse.Code, "LottoInfoResponse Code");
            AssertEqual(true, lottoInfoResponse.LottoInfos.Count > 0, "LottoInfoResponse LottoInfos non-empty");

            const int gachaInfoPacketId = 13_006;
            InvokeRegisteredRequestHandler(nameof(GetGachaInfoRequest), harness.Session, gachaInfoPacketId, new GetGachaInfoRequest { Id = 49 });
            GetGachaInfoResponse gachaInfoResponse = ReadResponsePayload<GetGachaInfoResponse>(
                harness,
                gachaInfoPacketId,
                nameof(GetGachaInfoResponse),
                "GetGachaInfoRequest Id 49 response");
            AssertEqual(0, gachaInfoResponse.Code, "GetGachaInfoResponse Code");
            AssertRequiredMemberNull(gachaInfoResponse, nameof(GetGachaInfoResponse.GridInfoList), "GetGachaInfoResponse GridInfoList retail empty payload");
            AssertRequiredMemberNull(gachaInfoResponse, nameof(GetGachaInfoResponse.GachaRecordList), "GetGachaInfoResponse GachaRecordList retail empty payload");
            AssertEqual(0, gachaInfoResponse.CurExchangeItemCount, "GetGachaInfoResponse CurExchangeItemCount");
            AssertRequiredMemberNull(gachaInfoResponse, nameof(GetGachaInfoResponse.GetRewardList), "GetGachaInfoResponse GetRewardList retail empty payload");
            AssertEqual(0, gachaInfoResponse.TotalTimes, "GetGachaInfoResponse TotalTimes");
            AssertEqual(0, gachaInfoResponse.MissTimes, "GetGachaInfoResponse MissTimes");

            const int gachaItemExchangePacketId = 13_007;
            InvokeRegisteredRequestHandler(
                nameof(GachaItemExchangeRequest),
                harness.Session,
                gachaItemExchangePacketId,
                new GachaItemExchangeRequest { Id = 49, ItemId = 96001, Count = 2 });
            GachaItemExchangeResponse gachaItemExchangeResponse = ReadResponsePayload<GachaItemExchangeResponse>(
                harness,
                gachaItemExchangePacketId,
                nameof(GachaItemExchangeResponse),
                "GachaItemExchangeRequest non-zero exchange response");
            AssertEqual(0, gachaItemExchangeResponse.Code, "GachaItemExchangeResponse Code");
            AssertEmptyList(gachaItemExchangeResponse.RewardGoodsList, "GachaItemExchangeResponse RewardGoodsList");

            ValidateRequestHandlerRegistration(nameof(PassportRecvAllRewardRequest));
            const int passportRewardPacketId = 13_008;
            InvokeRegisteredRequestHandler(nameof(PassportRecvAllRewardRequest), harness.Session, passportRewardPacketId, new PassportRecvAllRewardRequest());
            PassportRecvAllRewardResponse passportRewardResponse = ReadResponsePayload<PassportRecvAllRewardResponse>(
                harness,
                passportRewardPacketId,
                nameof(PassportRecvAllRewardResponse),
                "PassportRecvAllRewardRequest response");
            AssertEqual(0, passportRewardResponse.Code, "PassportRecvAllRewardResponse Code");
            if (passportRewardResponse.RewardList is null)
                throw new InvalidDataException("PassportRecvAllRewardResponse RewardList: expected initialized list.");
            if (passportRewardResponse.PassportInfos is null)
                throw new InvalidDataException("PassportRecvAllRewardResponse PassportInfos: expected initialized list.");

            const int mailDeletePacketId = 13_009;
            InvokeRegisteredRequestHandler(nameof(MailDeleteRequest), harness.Session, mailDeletePacketId, new MailDeleteRequest());
            MailDeleteResponse mailDeleteResponse = ReadResponsePayload<MailDeleteResponse>(
                harness,
                mailDeletePacketId,
                nameof(MailDeleteResponse),
                "MailDeleteRequest response");
            if (mailDeleteResponse.DelIdList is null)
                throw new InvalidDataException("MailDeleteResponse DelIdList: expected initialized list.");
        }

        private static void ValidatePurchaseRequestCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            const string requestName = nameof(PurchaseRequest);
            const string responseName = nameof(PurchaseResponse);
            const uint purchaseId = 90_943;
            const int purchaseCount = 1;
            int[] capturedUiTypes = [5, 6, 7, 8, 9, 10, 11, 12, 14, 15, 16];

            MethodInfo handlerMethod = GetRegisteredRequestHandlerMethod(requestName);
            AssertEqual("PurchaseRequestHandler", handlerMethod.Name, $"{requestName} registered handler method");

            PurchaseRequest request = new()
            {
                Count = purchaseCount,
                Param = null,
                Id = purchaseId,
                DiscountId = 0,
                UiTypeList = capturedUiTypes.ToList()
            };
            PurchaseRequest requestRoundTrip = MessagePackSerializer.Deserialize<PurchaseRequest>(
                MessagePackSerializer.Serialize(request));
            AssertEqual(purchaseCount, requestRoundTrip.Count, $"{requestName} Count MessagePack round-trip");
            if (requestRoundTrip.Param is not null)
                throw new InvalidDataException($"{requestName} Param MessagePack round-trip: expected captured nil Param.");
            AssertEqual(purchaseId, requestRoundTrip.Id, $"{requestName} Id MessagePack round-trip");
            AssertEqual(0, requestRoundTrip.DiscountId, $"{requestName} DiscountId MessagePack round-trip");
            AssertIntegerList(
                capturedUiTypes.Select(uiType => (long)uiType).ToArray(),
                requestRoundTrip.UiTypeList.Select(uiType => (long)uiType).ToArray(),
                $"{requestName} UiTypeList MessagePack round-trip");

            const long playerId = 88_009;
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PurchaseBuyTimes.Remove(purchaseId);
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                player,
                inventory,
                "purchase-request-compat-test");

            const int packetId = 13_013;
            InvokeRegisteredRequestHandler(requestName, harness.Session, packetId, requestRoundTrip);

            NotifyItemDataList rewardPush = ReadPushPayload<NotifyItemDataList>(
                harness,
                nameof(NotifyItemDataList),
                $"{requestName} reward inventory push");
            Item pushedReward = rewardPush.ItemDataList.Single(item => item.Id == 90_031);
            AssertEqual(1L, pushedReward.Count, $"{requestName} NotifyItemDataList reward item count");

            PurchaseResponse response = ReadResponsePayload<PurchaseResponse>(
                harness,
                packetId,
                responseName,
                $"{requestName} response");
            AssertEqual(0, response.Code, $"{responseName} Code");
            AssertEqual(1, response.RewardList.Count, $"{responseName} RewardList count");
            RewardGoods reward = response.RewardList[0];
            AssertEqual(90_031, reward.TemplateId, $"{responseName} RewardList[0].TemplateId");
            AssertEqual(1, reward.Count, $"{responseName} RewardList[0].Count");

            System.Collections.IDictionary purchaseInfo = RequiredDynamicMap(
                response.PurchaseInfo,
                $"{responseName} PurchaseInfo");
            AssertEqual((int)purchaseId, RequiredDynamicInteger(purchaseInfo, "Id", $"{responseName} PurchaseInfo"), $"{responseName} PurchaseInfo.Id");
            AssertEqual(1, RequiredDynamicInteger(purchaseInfo, "BuyTimes", $"{responseName} PurchaseInfo"), $"{responseName} PurchaseInfo.BuyTimes");

            System.Collections.IDictionary newPurchaseInfo = RequiredPurchaseInfoById(
                response.NewPurchaseInfoList,
                purchaseId,
                $"{responseName} NewPurchaseInfoList");
            AssertEqual(1, RequiredDynamicInteger(newPurchaseInfo, "BuyTimes", $"{responseName} NewPurchaseInfoList[{purchaseId}]"), $"{responseName} NewPurchaseInfoList[{purchaseId}].BuyTimes");

            if (!harness.Session.player.PurchaseBuyTimes.TryGetValue(purchaseId, out int persistedBuyTimes))
                throw new InvalidDataException($"{requestName}: expected Player.PurchaseBuyTimes to contain purchase id {purchaseId}.");
            AssertEqual(1, persistedBuyTimes, $"{requestName} persisted Player.PurchaseBuyTimes[{purchaseId}]");

            static System.Collections.IDictionary RequiredPurchaseInfoById(IEnumerable<object?> purchaseInfoList, uint requiredPurchaseId, string name)
            {
                foreach (object? purchaseInfo in purchaseInfoList)
                {
                    System.Collections.IDictionary purchaseInfoMap = RequiredDynamicMap(purchaseInfo, $"{name} item");
                    if (RequiredDynamicInteger(purchaseInfoMap, "Id", $"{name} item") == (int)requiredPurchaseId)
                        return purchaseInfoMap;
                }

                throw new InvalidDataException($"{name}: missing purchase id {requiredPurchaseId}.");
            }
        }



        private static CharacterData CreateLoginAccountCompatibilityCharacter(uint id, uint fashionId)
        {
            return new()
            {
                Id = id,
                Level = 80,
                Quality = 5,
                InitQuality = 5,
                Star = 5,
                Grade = 1,
                FashionId = fashionId,
                CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CharacterHeadInfo = new()
                {
                    HeadFashionId = fashionId,
                    HeadFashionType = 1
                }
            };
        }

        private static AscNet.Common.Database.Stage CreateLoginAccountCompatibilityStage(long uid)
        {
            return new()
            {
                Uid = uid,
                Stages = new(),
                Course = new(),
                FinishedTasks = new()
            };
        }

        private static void ValidateDrawCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForDrawCompatibility();
            AssertConstructShardTableCompatibility();
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
            const long drawHistoryPlayerId = 880008;
            const int drawHistoryPacketId = 8806;
            const long drawGroupHistoryPlayerId = 880009;
            const int drawGroupHistoryPacketId = 8807;

            DrawGetHistoryGroupListRequest historyRequest = new();
            DrawGetHistoryGroupListRequest historyRequestRoundTrip = MessagePackSerializer.Deserialize<DrawGetHistoryGroupListRequest>(
                MessagePackSerializer.Serialize(historyRequest));

            DrawGetHistoryGroupListResponse historyResponse;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(drawHistoryPlayerId),
                CreateDrawCompatibilityPlayer(drawHistoryPlayerId),
                CreateDrawCompatibilityInventory(drawHistoryPlayerId, []),
                "draw-history-group-list-compat-test"))
            {
                InvokeRegisteredRequestHandler("DrawGetHistoryGroupListRequest", harness.Session, drawHistoryPacketId, historyRequestRoundTrip);
                historyResponse = ReadResponsePayload<DrawGetHistoryGroupListResponse>(
                    harness,
                    drawHistoryPacketId,
                    nameof(DrawGetHistoryGroupListResponse),
                    "DrawGetHistoryGroupListRequest response");
            }

            AssertEqual(0, historyResponse.Code, "DrawGetHistoryGroupListResponse Code");
            if (historyResponse.HistoryGroups is null)
                throw new InvalidDataException("DrawGetHistoryGroupListResponse HistoryGroups: expected a live-schema list, got nil.");
            Dictionary<int, int> expectedHistoryGroupPriorities = new()
            {
                [memberTargetGroupId] = 9000,
                [weaponResearchGroupId] = 1000,
                [targetWeaponResearchGroupId] = 500,
                [themedEventConstructGroupId] = 21000,
                [arrivalConstructGroupId] = 14000,
                [fateArrivalConstructGroupId] = 13000,
                [fateThemedConstructGroupId] = 20000,
                [targetUniframeGroupId] = 100,
                [cubTargetGroupId] = 8000
            };
            Dictionary<int, int> actualHistoryGroupPriorities = new();
            foreach (DrawHistoryGroup historyGroup in historyResponse.HistoryGroups)
            {
                if (actualHistoryGroupPriorities.ContainsKey(historyGroup.DrawGroupId))
                    throw new InvalidDataException($"DrawGetHistoryGroupListResponse HistoryGroups: duplicate DrawGroupId {historyGroup.DrawGroupId}.");
                actualHistoryGroupPriorities.Add(historyGroup.DrawGroupId, historyGroup.Priority);
            }
            foreach (KeyValuePair<int, int> expectedHistoryGroup in expectedHistoryGroupPriorities.OrderBy(group => group.Key))
            {
                if (!actualHistoryGroupPriorities.TryGetValue(expectedHistoryGroup.Key, out int actualPriority))
                    throw new InvalidDataException($"DrawGetHistoryGroupListResponse HistoryGroups: missing DrawGroupId {expectedHistoryGroup.Key}.");
                AssertEqual(expectedHistoryGroup.Value, actualPriority, $"DrawGetHistoryGroupListResponse HistoryGroups[{expectedHistoryGroup.Key}] Priority");
            }


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

            DrawGetDrawInfoListResponse weaponResearchInfoResponse = ReadDrawInfoListForGroup(weaponResearchGroupId, drawInfoPacketId + 8, drawGroupPlayerId, "draw-weapon-research-info-compat-test");
            AssertWeaponResearchDraw201Info(weaponResearchInfoResponse, weaponResearchGroupId);

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
            AssertCurrentWeaponBannerShopFlags(
                new Dictionary<int, bool>
                {
                    [370] = false,
                    [371] = false,
                    [372] = false,
                    [374] = true,
                    [375] = false,
                    [376] = true,
                    [377] = false,
                    [378] = false,
                    [379] = false
                },
                infoResponse.DrawInfoList);
            AssertCurrentWeaponBannerDrawPreviewRows(
                [
                    348,
                    349,
                    350,
                    351,
                    353,
                    354,
                    355,
                    356,
                    357,
                    358,
                    359,
                    360,
                    361,
                    362,
                    363,
                    364,
                    365,
                    366,
                    367,
                    370,
                    371,
                    372,
                    374,
                    375,
                    376,
                    377,
                    378,
                    379
                ],
                infoResponse.DrawInfoList);
            AssertCurrentOwnableWeaponBreakthroughCoverage();
            AssertCurrentWeaponRewardRoutingAndRates(weaponResearchGroupId, targetWeaponResearchGroupId);
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
            AssertCurrentCharacterBannerDrawPreviewRows();
            AssertDraw1488RepresentativeRewardGoodsShowQualityHydrated();
            AssertDraw1488TargetCharacterRewardGoodsHydrated(eventConstruct1488);
            AssertDrawDrawCardHandlerRejectsInvalidOrUnaffordableRequests(eventConstruct1488);

            const int drawGroupHistorySubType = 5;
            DrawInfo preloadedHistoryDrawInfo = PreloadDrawProgressToBottomTimes(
                drawGroupHistoryPlayerId,
                eventConstruct1488,
                eventConstruct1488.MaxBottomTimes / 4,
                "DrawGroupGetHistoryRequest group 11 subtype 5");
            DrawGroupGetHistoryRequest groupHistoryRequest = new()
            {
                GroupId = themedEventConstructGroupId,
                GroupSubType = drawGroupHistorySubType
            };
            DrawGroupGetHistoryRequest groupHistoryRequestRoundTrip = MessagePackSerializer.Deserialize<DrawGroupGetHistoryRequest>(
                MessagePackSerializer.Serialize(groupHistoryRequest));
            AssertEqual(themedEventConstructGroupId, groupHistoryRequestRoundTrip.GroupId, "DrawGroupGetHistoryRequest GroupId MessagePack round-trip");
            AssertEqual(drawGroupHistorySubType, groupHistoryRequestRoundTrip.GroupSubType, "DrawGroupGetHistoryRequest GroupSubType MessagePack round-trip");

            DrawGroupGetHistoryResponse preDrawGroupHistoryResponse;
            DrawDrawCardRequest drawGroupHistoryDrawCardRequest = new()
            {
                DrawId = eventConstruct1488.Id,
                Count = 1,
                UseDrawTicketId = 0
            };
            DrawDrawCardRequest drawGroupHistoryDrawCardRequestRoundTrip = MessagePackSerializer.Deserialize<DrawDrawCardRequest>(
                MessagePackSerializer.Serialize(drawGroupHistoryDrawCardRequest));
            AssertEqual(eventConstruct1488.Id, drawGroupHistoryDrawCardRequestRoundTrip.DrawId, "DrawGroupGetHistoryRequest draw 1488 DrawDrawCardRequest DrawId MessagePack round-trip");
            AssertEqual(1, drawGroupHistoryDrawCardRequestRoundTrip.Count, "DrawGroupGetHistoryRequest draw 1488 DrawDrawCardRequest Count MessagePack round-trip");
            AssertEqual(0, drawGroupHistoryDrawCardRequestRoundTrip.UseDrawTicketId, "DrawGroupGetHistoryRequest draw 1488 DrawDrawCardRequest UseDrawTicketId MessagePack round-trip");

            DrawDrawCardResponse drawGroupHistoryDrawCardResponse = null!;
            DrawGroupGetHistoryResponse postDrawGroupHistoryResponse;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(drawGroupHistoryPlayerId),
                CreateDrawCompatibilityPlayer(drawGroupHistoryPlayerId),
                CreateDrawCompatibilityInventory(drawGroupHistoryPlayerId, [new Item { Id = eventConstruct1488.UseItemId, Count = eventConstruct1488.UseItemCount }]),
                "draw-group-history-compat-test"))
            {
                InvokeRegisteredRequestHandler(nameof(DrawGroupGetHistoryRequest), harness.Session, drawGroupHistoryPacketId, groupHistoryRequestRoundTrip);
                preDrawGroupHistoryResponse = ReadResponsePayload<DrawGroupGetHistoryResponse>(
                    harness,
                    drawGroupHistoryPacketId,
                    nameof(DrawGroupGetHistoryResponse),
                    "DrawGroupGetHistoryRequest pre-draw response");

                InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, drawGroupHistoryPacketId + 1, drawGroupHistoryDrawCardRequestRoundTrip);
                for (int packetIndex = 0; packetIndex < 6; packetIndex++)
                {
                    Packet packet = harness.ReadPacket($"DrawGroupGetHistoryRequest draw 1488 packet {packetIndex + 1}");
                    if (packet.Type == Packet.ContentType.Push)
                        continue;

                    AssertEqual(Packet.ContentType.Response, packet.Type, "DrawGroupGetHistoryRequest draw 1488 response packet type");
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(drawGroupHistoryPacketId + 1, response.Id, "DrawGroupGetHistoryRequest draw 1488 response packet id");
                    AssertEqual(nameof(DrawDrawCardResponse), response.Name, "DrawGroupGetHistoryRequest draw 1488 response packet name");
                    drawGroupHistoryDrawCardResponse = MessagePackSerializer.Deserialize<DrawDrawCardResponse>(response.Content);
                    goto FoundDrawGroupHistoryDrawCardResponse;
                }

                throw new InvalidDataException("DrawGroupGetHistoryRequest draw 1488: expected DrawDrawCardResponse after inventory pushes.");

            FoundDrawGroupHistoryDrawCardResponse:
                InvokeRegisteredRequestHandler(nameof(DrawGroupGetHistoryRequest), harness.Session, drawGroupHistoryPacketId + 2, groupHistoryRequestRoundTrip);
                postDrawGroupHistoryResponse = ReadResponsePayload<DrawGroupGetHistoryResponse>(
                    harness,
                    drawGroupHistoryPacketId + 2,
                    nameof(DrawGroupGetHistoryResponse),
                    "DrawGroupGetHistoryRequest post-draw response");
            }

            AssertEqual(0, preDrawGroupHistoryResponse.Code, "DrawGroupGetHistoryResponse pre-draw Code");
            AssertEmptyList(preDrawGroupHistoryResponse.HistoryRewardList, "DrawGroupGetHistoryResponse pre-draw HistoryRewardList");
            AssertEqual(preloadedHistoryDrawInfo.BottomTimes, preDrawGroupHistoryResponse.BottomTimes, "DrawGroupGetHistoryResponse pre-draw BottomTimes for draw 1488 progress");
            AssertEqual(60, preDrawGroupHistoryResponse.MaxBottomTimes, "DrawGroupGetHistoryResponse pre-draw MaxBottomTimes for draw 1488");

            AssertEqual(1, drawGroupHistoryDrawCardResponse.RewardGoodsList.Count, "DrawGroupGetHistoryRequest draw 1488 RewardGoodsList count for history");
            DrawInfo drawGroupHistoryClientDrawInfo = drawGroupHistoryDrawCardResponse.ClientDrawInfo
                ?? throw new InvalidDataException("DrawGroupGetHistoryRequest draw 1488: expected ClientDrawInfo after draw.");
            AssertEqual(eventConstruct1488.Id, drawGroupHistoryClientDrawInfo.Id, "DrawGroupGetHistoryRequest draw 1488 ClientDrawInfo Id");
            AssertEqual(preloadedHistoryDrawInfo.BottomTimes - 1, drawGroupHistoryClientDrawInfo.BottomTimes, "DrawGroupGetHistoryRequest draw 1488 ClientDrawInfo BottomTimes after 1x draw");

            AssertEqual(0, postDrawGroupHistoryResponse.Code, "DrawGroupGetHistoryResponse post-draw Code");
            if (postDrawGroupHistoryResponse.HistoryRewardList is null)
                throw new InvalidDataException("DrawGroupGetHistoryResponse post-draw HistoryRewardList: expected recorded draw history, got nil.");
            if (postDrawGroupHistoryResponse.HistoryRewardList.Count == 0)
                throw new InvalidDataException("DrawGroupGetHistoryResponse post-draw HistoryRewardList: expected recorded draw history for draw 1488, got an empty list.");
            AssertEqual(drawGroupHistoryDrawCardResponse.RewardGoodsList.Count, postDrawGroupHistoryResponse.HistoryRewardList.Count, "DrawGroupGetHistoryResponse post-draw HistoryRewardList count matches draw rewards");
            for (int historyIndex = 0; historyIndex < drawGroupHistoryDrawCardResponse.RewardGoodsList.Count; historyIndex++)
            {
                RewardGoods drawnReward = drawGroupHistoryDrawCardResponse.RewardGoodsList[historyIndex];
                DrawHistoryReward historyReward = postDrawGroupHistoryResponse.HistoryRewardList[historyIndex];
                RewardGoods recordedReward = historyReward.RewardGoods
                    ?? throw new InvalidDataException($"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods: expected recorded reward goods, got nil.");
                if (historyReward.DrawTime <= 0)
                    throw new InvalidDataException($"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].DrawTime: expected positive draw time, got {historyReward.DrawTime}.");

                AssertEqual(drawnReward.TemplateId, recordedReward.TemplateId, $"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods.TemplateId");
                AssertEqual(drawnReward.RewardType, recordedReward.RewardType, $"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods.RewardType");
                AssertEqual(drawnReward.Count, recordedReward.Count, $"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods.Count");
                AssertEqual(drawnReward.Level, recordedReward.Level, $"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods.Level");
                AssertEqual(drawnReward.Quality, recordedReward.Quality, $"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods.Quality");
                AssertEqual(drawnReward.Grade, recordedReward.Grade, $"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods.Grade");
                AssertEqual(drawnReward.Breakthrough, recordedReward.Breakthrough, $"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods.Breakthrough");
                AssertEqual(drawnReward.ConvertFrom, recordedReward.ConvertFrom, $"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods.ConvertFrom");
                AssertEqual(drawnReward.ShowQuality, recordedReward.ShowQuality, $"DrawGroupGetHistoryResponse post-draw HistoryRewardList[{historyIndex}].RewardGoods.ShowQuality");
            }
            AssertEqual(drawGroupHistoryClientDrawInfo.BottomTimes, postDrawGroupHistoryResponse.BottomTimes, "DrawGroupGetHistoryResponse post-draw BottomTimes tracks draw 1488 progress");
            AssertEqual(60, postDrawGroupHistoryResponse.MaxBottomTimes, "DrawGroupGetHistoryResponse post-draw MaxBottomTimes for draw 1488");
            AssertDraw1488DuplicatePityTenDraw(eventConstruct1488);
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
            AssertCurrentArrivalDrawInfoContents(
                arrivalInfoResponse.DrawInfoList,
                arrivalConstructGroupId,
                new Dictionary<int, int>
                {
                    [1492] = 1291003,
                    [1493] = 1381003,
                    [1494] = 1171004
                },
                expectedBanner: "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterNormalV4P5.prefab",
                expectedMaxBottomTimes: 60,
                expectedBottomTimes: 47,
                "DrawGetDrawInfoListResponse group 12 Arrival Construct");
            AssertCurrentArrivalDrawInfoContents(
                fateArrivalInfoResponse.DrawInfoList,
                fateArrivalConstructGroupId,
                new Dictionary<int, int>
                {
                    [2486] = 1291003,
                    [2487] = 1381003,
                    [2488] = 1171004
                },
                expectedBanner: "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/UiDrawCollaborationCharacterFateV4P5.prefab",
                expectedMaxBottomTimes: 100,
                expectedBottomTimes: 100,
                "DrawGetDrawInfoListResponse group 13 Fate Arrival Construct");
            AssertCurrentArrivalConstructRewardRouting();
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
                    1061004,
                    1251002,
                    1281002,
                    1291002,
                    1301002,
                    1311002,
                    1341002,
                    1351003,
                    1361003,
                    1371002
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
            AssertEqual(0, drawCardResponse.Code, "DrawDrawCardResponse Code");
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
            AssertDrawEquipRewardPushesRecycleFlag(drawInfoBeforeCard);

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

        private static void ValidateJetavieDaybreakTableCompatibility()
        {
            Dictionary<int, CharacterTable> characterRowsById = TableReaderV2.Parse<CharacterTable>().ToDictionary(character => character.Id);
            ILookup<int, CharacterSkillTable> skillRowsByCharacterId = TableReaderV2.Parse<CharacterSkillTable>().ToLookup(skill => skill.CharacterId);
            Dictionary<int, CharacterSkillGroupTable> skillGroupRowsById = TableReaderV2.Parse<CharacterSkillGroupTable>().ToDictionary(skillGroup => skillGroup.Id);
            ILookup<int, CharacterSkillLevelEffectTable> skillLevelEffectRowsBySkillId = TableReaderV2.Parse<CharacterSkillLevelEffectTable>()
                .ToLookup(skillLevelEffect => skillLevelEffect.SkillId);

            CharacterTable jetavieDaybreak = characterRowsById.TryGetValue(1341002, out CharacterTable? jetavieDaybreakRow)
                ? jetavieDaybreakRow
                : throw new InvalidDataException("Jetavie: Daybreak Character.tsv row 1341002: expected local row.");

            CharacterSkillTable[] jetavieDaybreakSkillRows = skillRowsByCharacterId[1341002].ToArray();
            AssertEqual(1, jetavieDaybreakSkillRows.Length, "Jetavie: Daybreak CharacterSkill.tsv row 1341002 row count");
            AssertJetavieDaybreakSkillMetadata(
                jetavieDaybreak,
                jetavieDaybreakSkillRows[0],
                skillGroupRowsById,
                skillLevelEffectRowsBySkillId,
                "Jetavie: Daybreak table compatibility");
        }


        private static void AssertConstructShardTableCompatibility()
        {
            List<CharacterTable> characterRows = TableReaderV2.Parse<CharacterTable>();
            Dictionary<int, CharacterTable> characterRowsById = characterRows.ToDictionary(character => character.Id);
            Dictionary<int, ItemTable> itemRowsById = TableReaderV2.Parse<ItemTable>().ToDictionary(item => item.Id);
            Dictionary<int, EquipTable> equipRowsById = TableReaderV2.Parse<EquipTable>().ToDictionary(equip => equip.Id);
            Dictionary<int, FashionTable> fashionRowsById = TableReaderV2.Parse<FashionTable>().ToDictionary(fashion => fashion.Id);
            ILookup<int, CharacterSkillTable> skillRowsByCharacterId = TableReaderV2.Parse<CharacterSkillTable>().ToLookup(skill => skill.CharacterId);
            Dictionary<int, CharacterSkillGroupTable> skillGroupRowsById = TableReaderV2.Parse<CharacterSkillGroupTable>().ToDictionary(skillGroup => skillGroup.Id);
            ILookup<int, CharacterSkillLevelEffectTable> skillLevelEffectRowsBySkillId = TableReaderV2.Parse<CharacterSkillLevelEffectTable>()
                .ToLookup(skillLevelEffect => skillLevelEffect.SkillId);
            ILookup<int, EquipBreakThroughTable> breakthroughRowsByEquipId = TableReaderV2.Parse<EquipBreakThroughTable>()
                .ToLookup(breakthrough => breakthrough.EquipId);
            ILookup<int, CharacterQualityTable> qualityRowsByCharacterId = TableReaderV2.Parse<CharacterQualityTable>().ToLookup(quality => quality.CharacterId);
            Dictionary<int, List<CharacterGradeTable>> gradeRowsByCharacterId = TableReaderV2.Parse<CharacterGradeTable>()
                .GroupBy(grade => grade.CharacterId)
                .ToDictionary(group => group.Key, group => group.OrderBy(grade => grade.Grade).ToList());
            Dictionary<int, string> expectedCurrentClientShardNames = new()
            {
                [562] = "Inver-Shard - Feral Scent",
                [563] = "Inver-Shard - Indomitus",
                [564] = "Inver-Shard - Echo",
                [565] = "Inver-Shard - Lost Lullaby",
                [566] = "Inver-Shard - BLACK★ROCK SHOOTER",
                [567] = "Inver-Shard - Epitaph",
                [568] = "Inver-Shard - Shukra",
                [569] = "Inver-Shard - Decryptor",
                [570] = "Inver-Shard - Oblivion",
                [571] = "Inver-Shard - Ardeo",
                [572] = "Inver-Shard - Solacetune",
                [573] = "Inver-Shard - Lucid Dreamer",
                [574] = "Inver-Shard - Pyroath",
                [575] = "Inver-Shard - Fulgor",
                [576] = "Inver-Shard - Startrail",
                [577] = "Inver-Shard - Parhelion",
                [578] = "Inver-Shard - Daemonissa",
                [579] = "Inver-Shard - Pianissimo",
                [580] = "Inver-Shard - Daybreak",
                [581] = "Inver-Shard - Geiravor",
                [582] = "Inver-Shard - Vergil",
                [583] = "Inver-Shard - Dante",
                [584] = "Inver-Shard - Crepuscule",
                [585] = "Inver-Shard - Secator",
                [586] = "Inver-Shard - Aegis",
                [587] = "Inver-Shard - Limpidity",
                [588] = "Inver-Shard - Spectre",
                [589] = "Inver-Shard - Rête",
                [590] = "Inver-Shard - Dirge",
                [591] = "Inver-Shard - Aeternion",
                [592] = "Inver-Shard - Inverse Crown"
            };

            int[] expectedCurrentClientPlayableCharacterIds =
            [
                1251002,
                1281002,
                1291002,
                1301002,
                1311002,
                1341002,
                1351003,
                1361003,
                1371002,
                1291003,
                1321003,
                1381003
            ];
            Dictionary<int, (string LogName, int ItemId)> expectedCurrentClientCharacterIdentities = new()
            {
                [1291003] = ("Teddy: Spectre", 588),
                [1311002] = ("Yata: Fulgor", 575),
                [1321003] = ("Ishmael: Parhelion", 577),
                [1341002] = ("Jetavie: Daybreak", 580),
                [1381003] = ("Veronica: Aegis", 586)
            };
            Dictionary<int, (string Name, string TradeName, string LogName, int ItemId, int EquipType, int EquipId, int DefaultFashionId, int CaptainSkillId, string Code)> expectedCurrentClientCharacterIdentityRows = new()
            {
                [1131004] = ("Vera", "Geiravor", "Vera: Geiravor", 581, 54, 2544001, 6005001, 113422, "BPN-13"),
                [1301002] = ("Bridget", "Ardeo", "Bridget: Ardeo", 571, 44, 2444001, 6004001, 130222, "BPO-42"),
                [1341002] = ("Jetavie", "Daybreak", "Jetavie: Daybreak", 580, 53, 2534001, 6004901, 132222, "MAV-01")
            };

            (int Id, string Label)[] expectedCompatibilityCharacterDependencyRows =
            [
                (1531004, "Selena: Capriccio compatibility character"),
                (1231002, "Bambinata: Vitrum compatibility character"),
                (1241002, "Hanying: Zitherwoe compatibility character"),
                (1221003, "No. 21: Feral compatibility character"),
                (1251002, "Noctis: Indomitus compatibility character"),
                (1261003, "Alisa: Echo compatibility character"),
                (1271003, "Lamia: Lost Lullaby compatibility character"),
                (1081004, "Watanabe: Epitaph compatibility character"),
                (1281002, "BLACK★ROCK SHOOTER compatibility character"),
                (1521004, "Qu: Shukra compatibility character"),
                (1211003, "Wanshi: Lucid Dreamer compatibility character"),
                (1291002, "Teddy: Decryptor compatibility character"),
                (1171004, "Luna: Oblivion compatibility character"),
                (1021006, "Lucia: Pyroath compatibility character"),
                (1051005, "Nanami: Startrail compatibility character"),
                (1241003, "Hanying: Solacetune compatibility character"),
                (1311002, "Yata: Fulgor compatibility character"),
                (1321003, "Ishmael: Parhelion compatibility character"),
                (1331003, "Lilith: Daemonissa compatibility character"),
                (1041005, "Bianca: Crepuscule compatibility character"),
                (1531005, "Selena: Pianissimo compatibility character"),
                (1141004, "Rosetta: Arete compatibility character"),
                (1351003, "Vergil compatibility character"),
                (1061004, "Kamui: Aeternion compatibility character"),
                (1361003, "Dante compatibility character"),
                (1371002, "Discord: Secator compatibility character"),
                (1381003, "Veronica: Aegis compatibility character"),
                (1031005, "Liv: Limpidity compatibility character"),
                (1291003, "Teddy: Spectre compatibility character"),
                (1391003, "Nirvatia: Dirge compatibility character"),
                (1021007, "Lucia: Inverse Crown compatibility character")
            ];
            AssertCompatibilityCharacterDependencyRows(
                expectedCompatibilityCharacterDependencyRows,
                characterRowsById,
                itemRowsById,
                equipRowsById,
                fashionRowsById,
                skillRowsByCharacterId,
                breakthroughRowsByEquipId);

            for (int shardItemId = 562; shardItemId <= 592; shardItemId++)
            {
                if (!expectedCurrentClientShardNames.TryGetValue(shardItemId, out string? expectedName))
                    throw new InvalidDataException($"Current client construct shard Item.tsv ids: expected assertion coverage for shard item {shardItemId}.");
                ItemTable shardItem = itemRowsById.TryGetValue(shardItemId, out ItemTable? shardItemRow)
                    ? shardItemRow
                    : throw new InvalidDataException($"Current client construct shard Item.tsv ids 562..592: missing item row {shardItemId}.");
                AssertConstructShardItem(shardItem, $"Current client construct shard Item.tsv row {shardItemId}");
                AssertEqual(expectedName, shardItem.Name, $"Current client construct shard Item.tsv row {shardItemId} Name");
            }

            foreach (int characterId in expectedCurrentClientPlayableCharacterIds)
            {
                CharacterTable character = characterRowsById.TryGetValue(characterId, out CharacterTable? characterRow)
                    ? characterRow
                    : throw new InvalidDataException($"Current client playable Character.tsv row {characterId}: expected local row.");
                AssertEqual(1, character.Type, $"Current client playable Character.tsv row {characterId} Type");
            }

            if (characterRowsById.ContainsKey(1341003))
                throw new InvalidDataException("Current client playable Character.tsv rows: stale synthetic row 1341003 must not be present.");

            foreach ((int characterId, (string expectedLogName, int expectedItemId)) in expectedCurrentClientCharacterIdentities)
            {
                CharacterTable character = characterRowsById.TryGetValue(characterId, out CharacterTable? characterRow)
                    ? characterRow
                    : throw new InvalidDataException($"Current client Character.tsv identity row {characterId}: expected local row.");
                AssertEqual(expectedLogName, character.LogName, $"Current client Character.tsv row {characterId} LogName");
                AssertEqual(expectedItemId, character.ItemId, $"Current client Character.tsv row {characterId} ItemId");
            }

            foreach ((int characterId, (string expectedName, string expectedTradeName, string expectedLogName, int expectedItemId, int expectedEquipType, int expectedEquipId, int expectedDefaultFashionId, int expectedCaptainSkillId, string expectedCode)) in expectedCurrentClientCharacterIdentityRows)
            {
                CharacterTable character = characterRowsById.TryGetValue(characterId, out CharacterTable? characterRow)
                    ? characterRow
                    : throw new InvalidDataException($"Current client Character.tsv identity row {characterId}: expected local row.");
                AssertEqual(expectedName, character.Name, $"Current client Character.tsv row {characterId} Name");
                AssertEqual(expectedTradeName, character.TradeName, $"Current client Character.tsv row {characterId} TradeName");
                AssertEqual(expectedLogName, character.LogName, $"Current client Character.tsv row {characterId} LogName");
                AssertEqual(expectedItemId, character.ItemId, $"Current client Character.tsv row {characterId} ItemId");
                AssertEqual(expectedEquipType, character.EquipType, $"Current client Character.tsv row {characterId} EquipType");
                AssertEqual(expectedEquipId, character.EquipId, $"Current client Character.tsv row {characterId} EquipId");
                AssertEqual(expectedDefaultFashionId, character.DefaultNpcFashtionId, $"Current client Character.tsv row {characterId} DefaultNpcFashtionId");
                AssertEqual(expectedCaptainSkillId, character.CaptainSkillId, $"Current client Character.tsv row {characterId} CaptainSkillId");
                AssertEqual(expectedCode, character.Code, $"Current client Character.tsv row {characterId} Code");

                EquipTable defaultEquip = equipRowsById.TryGetValue(expectedEquipId, out EquipTable? equipRow)
                    ? equipRow
                    : throw new InvalidDataException($"Current client Character.tsv row {characterId}: expected Equip.tsv row {expectedEquipId} for Character.tsv EquipId.");
                AssertEqual(expectedEquipType, defaultEquip.Type, $"Current client Character.tsv row {characterId} default Equip.tsv Type");

                FashionTable defaultFashion = fashionRowsById.TryGetValue(expectedDefaultFashionId, out FashionTable? fashionRow)
                    ? fashionRow
                    : throw new InvalidDataException($"Current client Character.tsv row {characterId}: expected Fashion.tsv row {expectedDefaultFashionId} for Character.tsv DefaultNpcFashtionId.");
                AssertEqual(characterId, defaultFashion.CharacterId, $"Current client Character.tsv row {characterId} default Fashion.tsv CharacterId");
            }

            CharacterTable jetavieDaybreak = characterRowsById.TryGetValue(1341002, out CharacterTable? jetavieDaybreakRow)
                ? jetavieDaybreakRow
                : throw new InvalidDataException("Jetavie: Daybreak Character.tsv row 1341002: expected local row.");
            CharacterSkillTable jetavieDaybreakSkill = skillRowsByCharacterId[1341002].SingleOrDefault()
                ?? throw new InvalidDataException("Jetavie: Daybreak CharacterSkill.tsv row 1341002: expected local row.");
            AssertJetavieDaybreakSkillMetadata(
                jetavieDaybreak,
                jetavieDaybreakSkill,
                skillGroupRowsById,
                skillLevelEffectRowsBySkillId,
                "Jetavie: Daybreak current table metadata");

            foreach (CharacterTable character in characterRows.Where(character => character.Type == 1))
            {
                if (character.ItemId <= 0)
                    throw new InvalidDataException($"Construct Character.tsv row {character.Id} {character.Name}: expected positive ItemId, got {character.ItemId}.");
                ItemTable shardItem = itemRowsById.TryGetValue(character.ItemId, out ItemTable? shardItemRow)
                    ? shardItemRow
                    : throw new InvalidDataException($"Construct Character.tsv row {character.Id} {character.Name}: expected Item.tsv row {character.ItemId}.");
                AssertConstructShardItem(shardItem, $"Construct Character.tsv row {character.Id} {character.Name} ItemId {character.ItemId}");

                if (!skillRowsByCharacterId[character.Id].Any())
                    throw new InvalidDataException($"Playable Character.tsv row {character.Id} {character.Name}: expected at least one CharacterSkillTable row.");
                if (!qualityRowsByCharacterId[character.Id].Any())
                    throw new InvalidDataException($"Playable Character.tsv row {character.Id} {character.Name}: expected at least one CharacterQualityTable row.");
                List<CharacterGradeTable> characterGradeRows = gradeRowsByCharacterId.TryGetValue(character.Id, out List<CharacterGradeTable>? gradeRows)
                    ? gradeRows
                    : throw new InvalidDataException($"Playable Character.tsv row {character.Id} {character.Name}: expected CharacterGradeTable rows.");
                AssertEqual(14, characterGradeRows.Count, $"Playable Character.tsv row {character.Id} {character.Name} CharacterGradeTable row count");
                AssertIntegerList(
                    Enumerable.Range(1, 14).Select(grade => (long)grade).ToArray(),
                    characterGradeRows.Select(grade => (long)grade.Grade).ToArray(),
                    $"Playable Character.tsv row {character.Id} {character.Name} CharacterGradeTable sequential grades");
            }
        }

        private static void AssertCompatibilityCharacterDependencyRows(
            IReadOnlyList<(int Id, string Label)> expectedCharacterRows,
            IReadOnlyDictionary<int, CharacterTable> characterRowsById,
            IReadOnlyDictionary<int, ItemTable> itemRowsById,
            IReadOnlyDictionary<int, EquipTable> equipRowsById,
            IReadOnlyDictionary<int, FashionTable> fashionRowsById,
            ILookup<int, CharacterSkillTable> skillRowsByCharacterId,
            ILookup<int, EquipBreakThroughTable> breakthroughRowsByEquipId)
        {
            foreach ((int characterId, string label) in expectedCharacterRows)
            {
                CharacterTable character = characterRowsById.TryGetValue(characterId, out CharacterTable? characterRow)
                    ? characterRow
                    : throw new InvalidDataException($"{label} Character.tsv row {characterId}: expected compatibility row.");

                if (character.ItemId <= 0)
                    throw new InvalidDataException($"{label} Character.tsv row {characterId}: expected positive ItemId, got {character.ItemId}.");
                if (!itemRowsById.ContainsKey(character.ItemId))
                    throw new InvalidDataException($"{label} Character.tsv row {characterId}: expected Item.tsv row {character.ItemId} for Character.tsv ItemId.");

                if (character.EquipId <= 0)
                    throw new InvalidDataException($"{label} Character.tsv row {characterId}: expected positive EquipId, got {character.EquipId}.");
                EquipTable defaultEquip = equipRowsById.TryGetValue(character.EquipId, out EquipTable? equipRow)
                    ? equipRow
                    : throw new InvalidDataException($"{label} Character.tsv row {characterId}: expected Equip.tsv row {character.EquipId} for Character.tsv EquipId.");
                AssertEqual(character.EquipType, defaultEquip.Type, $"{label} default Equip.tsv Type matches Character.tsv EquipType");

                if (character.DefaultNpcFashtionId <= 0)
                    throw new InvalidDataException($"{label} Character.tsv row {characterId}: expected nonzero DefaultNpcFashtionId.");
                FashionTable defaultFashion = fashionRowsById.TryGetValue(character.DefaultNpcFashtionId, out FashionTable? fashionRow)
                    ? fashionRow
                    : throw new InvalidDataException($"{label} Character.tsv row {characterId}: expected Fashion.tsv row {character.DefaultNpcFashtionId} for Character.tsv DefaultNpcFashtionId.");
                AssertEqual(characterId, defaultFashion.CharacterId, $"{label} default Fashion.tsv CharacterId");
                AssertCompatibilityDefaultFashionVisuals(defaultFashion, $"{label} default Fashion.tsv row {defaultFashion.Id}");

                if (!skillRowsByCharacterId[characterId].Any())
                    throw new InvalidDataException($"{label} Character.tsv row {characterId}: expected CharacterSkill.tsv row.");
                if (!breakthroughRowsByEquipId[character.EquipId].Any())
                    throw new InvalidDataException($"{label} Character.tsv row {characterId}: expected EquipBreakThrough.tsv rows for default EquipId {character.EquipId}.");
            }
        }

        private static void AssertCompatibilityDefaultFashionVisuals(FashionTable fashion, string name)
        {
            AssertNonEmptyFashionAsset(fashion.Icon, $"{name} Icon");
            AssertNonEmptyFashionAsset(fashion.BigIcon, $"{name} BigIcon");
            AssertNonEmptyFashionAsset(fashion.SmallHeadIcon, $"{name} SmallHeadIcon");
            AssertNonEmptyFashionAsset(fashion.SmallHeadIconFashion, $"{name} SmallHeadIconFashion");
            AssertNonEmptyFashionAsset(fashion.BigHeadIcon, $"{name} BigHeadIcon");
            AssertNonEmptyFashionAsset(fashion.BigHeadIconFashion, $"{name} BigHeadIconFashion");
            AssertNonEmptyFashionAsset(fashion.RoundnessNotItemHeadIcon, $"{name} RoundnessNotItemHeadIcon");
            AssertNonEmptyFashionAsset(fashion.RoundnessHeadIcon, $"{name} RoundnessHeadIcon");
            AssertNonEmptyFashionAsset(fashion.BigRoundnessHeadIcon, $"{name} BigRoundnessHeadIcon");
            AssertNonEmptyFashionAsset(fashion.HalfBodyImage, $"{name} HalfBodyImage");
            AssertNonEmptyFashionAsset(fashion.RoleCharacterBigImage, $"{name} RoleCharacterBigImage");
            AssertNonEmptyFashionAsset(fashion.CharacterIcon, $"{name} CharacterIcon");
        }

        private static void AssertNonEmptyFashionAsset(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"{name}: expected a non-empty visual/icon asset path.");
        }

        private static void AssertConstructShardItem(ItemTable shardItem, string name)
        {
            if (!shardItem.Name.StartsWith("Inver-Shard -", StringComparison.Ordinal))
                throw new InvalidDataException($"{name}: expected Inver-Shard item name, got '{shardItem.Name}'.");
            AssertEqual(8, shardItem.ItemType, $"{name} ItemType");
            AssertEqual(4, shardItem.Quality, $"{name} Quality");
            AssertEqual(999, shardItem.MaxCount, $"{name} MaxCount");
            AssertEqual(999, shardItem.GridCount, $"{name} GridCount");
        }

        private static void AssertJetavieDaybreakSkillMetadata(
            CharacterTable characterRow,
            CharacterSkillTable skillRow,
            IReadOnlyDictionary<int, CharacterSkillGroupTable> skillGroupRowsById,
            ILookup<int, CharacterSkillLevelEffectTable> skillLevelEffectRowsBySkillId,
            string name)
        {
            const int jetavieDaybreakCharacterId = 1341002;
            (int SkillGroupId, int SkillId)[] expectedSkillRows =
            [
                (1322010, 132201),
                (1322060, 132206),
                (1322110, 132211),
                (1322160, 132216),
                (1322210, 132221),
                (1322170, 132217),
                (1322180, 132218),
                (1322220, 132222),
                (1322230, 132223),
                (1322240, 132224),
                (1322250, 132225),
                (1322260, 132226),
                (1322270, 132227)
            ];

            AssertEqual(jetavieDaybreakCharacterId, characterRow.Id, $"{name} Character.tsv Id");
            AssertEqual(132222, characterRow.CaptainSkillId, $"{name} Character.tsv CaptainSkillId");
            AssertEqual(jetavieDaybreakCharacterId, skillRow.CharacterId, $"{name} CharacterSkill.tsv CharacterId");
            AssertIntegerList(
                expectedSkillRows.Select(skill => (long)skill.SkillGroupId).ToArray(),
                skillRow.SkillGroupId.Where(skillGroupId => skillGroupId > 0).Select(skillGroupId => (long)skillGroupId).ToArray(),
                $"{name} CharacterSkill.tsv SkillGroupId");

            foreach ((int expectedSkillGroupId, int expectedSkillId) in expectedSkillRows)
            {
                CharacterSkillGroupTable skillGroup = skillGroupRowsById.TryGetValue(expectedSkillGroupId, out CharacterSkillGroupTable? skillGroupRow)
                    ? skillGroupRow
                    : throw new InvalidDataException($"{name} CharacterSkillGroup.tsv row {expectedSkillGroupId}: expected local row.");
                if (!skillGroup.SkillId.Contains(expectedSkillId))
                    throw new InvalidDataException($"{name} CharacterSkillGroup.tsv row {expectedSkillGroupId}: expected SkillId {expectedSkillId}.");
                if (!skillLevelEffectRowsBySkillId[expectedSkillId].Any(skillLevelEffect => skillLevelEffect.Level == 1))
                    throw new InvalidDataException($"{name} CharacterSkillLevelEffect.tsv SkillId {expectedSkillId}: expected at least one level 1 row.");
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

        private static void AssertWeaponResearchDraw201Info(DrawGetDrawInfoListResponse response, int expectedGroupId)
        {
            const string name = "DrawGetDrawInfoListResponse group 2 weapon research draw 201";
            AssertEqual(0, response.Code, "DrawGetDrawInfoListResponse group 2 Code");
            AssertEqual(1, response.DrawInfoList.Count, "DrawGetDrawInfoListResponse group 2 retail draw info count");
            DrawInfo drawInfo = response.DrawInfoList.Single();
            AssertEqual(1, drawInfo.DrawType, $"{name} DrawType");
            AssertEqual(50001, drawInfo.UseItemId, $"{name} UseItemId");
            AssertEqual(250, drawInfo.UseItemCount, $"{name} UseItemCount");
            AssertEqual(17, drawInfo.BottomTimes, $"{name} BottomTimes");
            AssertEqual(30, drawInfo.MaxBottomTimes, $"{name} MaxBottomTimes");
            AssertRetailDrawInfoArt(
                drawInfo,
                201,
                expectedGroupId,
                "Assets/Product/Ui/ComponentPrefab/DrawCollaboration/DrawCollaboration07.prefab",
                new Dictionary<int, string>(),
                new Dictionary<int, int>(),
                [],
                0,
                name);
            AssertIntegerList([], drawInfo.PurchaseId.Select(purchaseId => (long)purchaseId).ToArray(), $"{name} PurchaseId");
            AssertEqual(0, drawInfo.CapacityCheckType, $"{name} CapacityCheckType");
            AssertEqual(false, drawInfo.IsShowShop, $"{name} IsShowShop");
        }

        private static void AssertCurrentArrivalDrawInfoContents(
            IReadOnlyList<DrawInfo> drawInfos,
            int expectedGroupId,
            IReadOnlyDictionary<int, int> expectedTargetIdsByDrawId,
            string expectedBanner,
            int expectedMaxBottomTimes,
            int expectedBottomTimes,
            string name)
        {
            AssertEqual(expectedTargetIdsByDrawId.Count, drawInfos.Count, $"{name} retail draw info count");
            long[] expectedPurchaseIds = [1969, 2063, 2064, 2059, 2060, 2061, 2062, 2065, 2066, 2067, 2068, 2069, 2070, 2072];
            foreach ((int drawId, int expectedTargetId) in expectedTargetIdsByDrawId.OrderBy(entry => entry.Key))
            {
                DrawInfo drawInfo = drawInfos.Single(info => info.Id == drawId);
                string drawName = $"{name} draw {drawId}";
                AssertEqual(3, drawInfo.DrawType, $"{drawName} DrawType");
                AssertEqual(50005, drawInfo.UseItemId, $"{drawName} UseItemId");
                AssertEqual(250, drawInfo.UseItemCount, $"{drawName} UseItemCount");
                AssertEqual(expectedMaxBottomTimes, drawInfo.MaxBottomTimes, $"{drawName} MaxBottomTimes");
                AssertEqual(expectedBottomTimes, drawInfo.BottomTimes, $"{drawName} BottomTimes");
                AssertEqual(1782370800L, drawInfo.StartTime, $"{drawName} StartTime");
                AssertEqual(1783580340L, drawInfo.EndTime, $"{drawName} EndTime");
                AssertRetailDrawInfoArt(
                    drawInfo,
                    drawId,
                    expectedGroupId,
                    expectedBanner,
                    new Dictionary<int, string>(),
                    new Dictionary<int, int> { [1] = expectedTargetId },
                    [5, 6, 2],
                    0,
                    drawName);
                AssertIntegerList(expectedPurchaseIds, drawInfo.PurchaseId.Select(purchaseId => (long)purchaseId).ToArray(), $"{drawName} PurchaseId");
            }
        }

        private static void AssertCurrentArrivalConstructRewardRouting()
        {
            Type drawManagerType = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
            Type drawInfoTemplateType = drawManagerType.GetNestedType("DrawInfoTemplate", BindingFlags.NonPublic)
                ?? throw new MissingMemberException(drawManagerType.FullName, "DrawInfoTemplate");
            MethodInfo drawRetailReward = RequiredMethod(
                drawManagerType,
                "DrawRetailReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(bool)]);
            MethodInfo drawCharacterReward = RequiredMethod(
                drawManagerType,
                "DrawCharacterReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(bool)]);
            MethodInfo drawLegacyCharacterReward = RequiredMethod(
                drawManagerType,
                "DrawLegacyCharacterReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(bool)]);
            MethodInfo drawEquipReward = RequiredMethod(
                drawManagerType,
                "DrawEquipReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(bool)]);
            MethodInfo drawFallbackItemReward = RequiredMethod(
                drawManagerType,
                "DrawFallbackItemReward",
                BindingFlags.Static | BindingFlags.NonPublic);

            List<IlInstruction> drawRetailRewardInstructions = ReadIlInstructions(drawRetailReward).ToList();
            MethodInfo[] routedRewardMethods = [drawCharacterReward, drawLegacyCharacterReward, drawEquipReward, drawFallbackItemReward];
            AssertDrawRetailRewardRoutesGroup(
                drawRetailRewardInstructions,
                routedRewardMethods,
                groupId: 12,
                expectedMethod: drawCharacterReward,
                "Draw group 12 normal Arrival Construct reward routing");
            AssertDrawRetailRewardRoutesGroup(
                drawRetailRewardInstructions,
                routedRewardMethods,
                groupId: 13,
                expectedMethod: drawLegacyCharacterReward,
                "Draw group 13 Fate Arrival Construct reward routing");
            AssertNormalCharacterRewardRetailRates(drawCharacterReward, "Draw group 12 normal Arrival Construct reward rates");
            AssertLegacyCharacterRewardRetailRates(drawLegacyCharacterReward, "Draw group 13 Fate Arrival Construct reward rates");
        }

        private static void AssertCurrentWeaponRewardRoutingAndRates(int weaponResearchGroupId, int targetWeaponResearchGroupId)
        {
            Type drawManagerType = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
            Type drawInfoTemplateType = drawManagerType.GetNestedType("DrawInfoTemplate", BindingFlags.NonPublic)
                ?? throw new MissingMemberException(drawManagerType.FullName, "DrawInfoTemplate");
            MethodInfo drawRetailReward = RequiredMethod(
                drawManagerType,
                "DrawRetailReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(bool)]);
            MethodInfo drawCharacterReward = RequiredMethod(
                drawManagerType,
                "DrawCharacterReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(bool)]);
            MethodInfo drawLegacyCharacterReward = RequiredMethod(
                drawManagerType,
                "DrawLegacyCharacterReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(bool)]);
            MethodInfo drawEquipReward = RequiredMethod(
                drawManagerType,
                "DrawEquipReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType, typeof(bool)]);
            MethodInfo drawPreviewEquipReward = RequiredMethod(
                drawManagerType,
                "DrawPreviewEquipReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType]);
            MethodInfo drawRandomWeaponReward = RequiredMethod(
                drawManagerType,
                "DrawRandomWeaponReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(int), typeof(int)]);
            MethodInfo drawFallbackItemReward = RequiredMethod(
                drawManagerType,
                "DrawFallbackItemReward",
                BindingFlags.Static | BindingFlags.NonPublic);

            List<IlInstruction> drawRetailRewardInstructions = ReadIlInstructions(drawRetailReward).ToList();
            MethodInfo[] routedRewardMethods = [drawCharacterReward, drawLegacyCharacterReward, drawEquipReward, drawFallbackItemReward];
            AssertDrawRetailRewardRoutesGroup(
                drawRetailRewardInstructions,
                routedRewardMethods,
                weaponResearchGroupId,
                drawEquipReward,
                "Draw group 2 Weapon Research reward routing");
            AssertDrawRetailRewardRoutesGroup(
                drawRetailRewardInstructions,
                routedRewardMethods,
                targetWeaponResearchGroupId,
                drawEquipReward,
                "Draw group 4 Target Weapon Research reward routing");
            AssertEquipRewardRetailRates(
                drawEquipReward,
                drawPreviewEquipReward,
                drawRandomWeaponReward,
                "Draw group 2/4 weapon reward rates");
        }


        private enum IlVirtualValue
        {
            TemplateArgument,
            ForceRareArgument,
            Unknown
        }

        private static void AssertDrawRetailRewardRoutesGroup(
            IReadOnlyList<IlInstruction> instructions,
            IReadOnlyList<MethodInfo> routedRewardMethods,
            int groupId,
            MethodInfo expectedMethod,
            string name)
        {
            MethodInfo groupIdGetter = routedRewardMethods[0].GetParameters()[0].ParameterType
                .GetProperty("GroupId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetMethod
                ?? throw new MissingMemberException(routedRewardMethods[0].GetParameters()[0].ParameterType.FullName, "GroupId");
            MethodInfo routedMethod = ResolveFirstCalledCandidateForGroup(instructions, groupIdGetter, groupId, routedRewardMethods, name)
                ?? throw new InvalidDataException($"{name}: expected group {groupId} route to call a retail reward branch.");
            if (!MethodsMatch(routedMethod, expectedMethod))
                throw new InvalidDataException($"{name}: expected group {groupId} to route to {expectedMethod.Name}, got {routedMethod.Name}.");
        }

        private static MethodInfo? ResolveFirstCalledCandidateForGroup(
            IReadOnlyList<IlInstruction> instructions,
            MethodInfo groupIdGetter,
            int groupId,
            IReadOnlyList<MethodInfo> candidates,
            string name)
        {
            Stack<object?> stack = new();
            Dictionary<int, object?> locals = new();
            int index = 0;
            for (int inspected = 0; inspected < 256; inspected++)
            {
                if (index < 0 || index >= instructions.Count)
                    return null;

                IlInstruction instruction = instructions[index];
                if (LdcI4Value(instruction) is int intValue)
                {
                    stack.Push(intValue);
                    index++;
                    continue;
                }
                if (IsLoadArgument(instruction, 0))
                {
                    stack.Push(IlVirtualValue.TemplateArgument);
                    index++;
                    continue;
                }
                if (IsLoadArgument(instruction, 1))
                {
                    stack.Push(IlVirtualValue.ForceRareArgument);
                    index++;
                    continue;
                }
                if (LoadLocalIndex(instruction) is int loadLocalIndex)
                {
                    stack.Push(locals.GetValueOrDefault(loadLocalIndex, IlVirtualValue.Unknown));
                    index++;
                    continue;
                }
                if (StoreLocalIndex(instruction) is int storeLocalIndex)
                {
                    locals[storeLocalIndex] = PopIlValue(stack, name);
                    index++;
                    continue;
                }
                if (instruction.OpCode == OpCodes.Sub)
                {
                    int right = PopIlInt(stack, name);
                    int left = PopIlInt(stack, name);
                    stack.Push(left - right);
                    index++;
                    continue;
                }
                if (instruction.OpCode == OpCodes.Ceq)
                {
                    object? right = PopIlValue(stack, name);
                    object? left = PopIlValue(stack, name);
                    stack.Push(Equals(left, right) ? 1 : 0);
                    index++;
                    continue;
                }
                if (instruction.OpCode == OpCodes.Cgt || instruction.OpCode == OpCodes.Cgt_Un)
                {
                    int right = PopIlInt(stack, name);
                    int left = PopIlInt(stack, name);
                    stack.Push(left > right ? 1 : 0);
                    index++;
                    continue;
                }
                if (instruction.OpCode == OpCodes.Clt || instruction.OpCode == OpCodes.Clt_Un)
                {
                    int right = PopIlInt(stack, name);
                    int left = PopIlInt(stack, name);
                    stack.Push(left < right ? 1 : 0);
                    index++;
                    continue;
                }
                if (instruction.OpCode == OpCodes.Switch && instruction.Operand is int[] switchTargets)
                {
                    int switchValue = PopIlInt(stack, name);
                    int targetOffset = switchValue >= 0 && switchValue < switchTargets.Length
                        ? switchTargets[switchValue]
                        : instructions[index + 1].Offset;
                    index = FindIlInstructionIndexByOffset(instructions, targetOffset, name);
                    continue;
                }
                if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch && instruction.Operand is int conditionalTargetOffset)
                {
                    bool branchTaken = EvaluateConditionalBranch(instruction.OpCode, stack, name);
                    index = branchTaken
                        ? FindIlInstructionIndexByOffset(instructions, conditionalTargetOffset, name)
                        : index + 1;
                    continue;
                }
                if (instruction.OpCode.FlowControl == FlowControl.Branch && instruction.Operand is int branchTargetOffset)
                {
                    index = FindIlInstructionIndexByOffset(instructions, branchTargetOffset, name);
                    continue;
                }
                if (instruction.Operand is MethodBase calledMethod)
                {
                    MethodInfo? candidate = candidates.FirstOrDefault(candidate => MethodsMatch(calledMethod, candidate));
                    if (candidate is not null)
                        return candidate;
                    if (MethodsMatch(calledMethod, groupIdGetter))
                    {
                        _ = PopIlValue(stack, name);
                        stack.Push(groupId);
                        index++;
                        continue;
                    }

                    stack.Clear();
                    stack.Push(IlVirtualValue.Unknown);
                    index++;
                    continue;
                }
                if (instruction.OpCode.FlowControl == FlowControl.Return || instruction.OpCode == OpCodes.Throw)
                    return null;

                index++;
            }

            throw new InvalidDataException($"{name}: expected reward routing branch to resolve within 256 IL instructions.");
        }

        private static bool EvaluateConditionalBranch(OpCode opCode, Stack<object?> stack, string name)
        {
            if (opCode == OpCodes.Brtrue || opCode == OpCodes.Brtrue_S)
                return PopIlInt(stack, name) != 0;
            if (opCode == OpCodes.Brfalse || opCode == OpCodes.Brfalse_S)
                return PopIlInt(stack, name) == 0;

            int right = PopIlInt(stack, name);
            int left = PopIlInt(stack, name);
            if (opCode == OpCodes.Beq || opCode == OpCodes.Beq_S)
                return left == right;
            if (opCode == OpCodes.Bne_Un || opCode == OpCodes.Bne_Un_S)
                return left != right;
            if (opCode == OpCodes.Blt || opCode == OpCodes.Blt_S || opCode == OpCodes.Blt_Un || opCode == OpCodes.Blt_Un_S)
                return left < right;
            if (opCode == OpCodes.Ble || opCode == OpCodes.Ble_S || opCode == OpCodes.Ble_Un || opCode == OpCodes.Ble_Un_S)
                return left <= right;
            if (opCode == OpCodes.Bgt || opCode == OpCodes.Bgt_S || opCode == OpCodes.Bgt_Un || opCode == OpCodes.Bgt_Un_S)
                return left > right;
            if (opCode == OpCodes.Bge || opCode == OpCodes.Bge_S || opCode == OpCodes.Bge_Un || opCode == OpCodes.Bge_Un_S)
                return left >= right;

            throw new InvalidDataException($"{name}: unsupported conditional branch opcode {opCode}.");
        }

        private static object? PopIlValue(Stack<object?> stack, string name)
        {
            if (stack.Count == 0)
                throw new InvalidDataException($"{name}: expected value on IL evaluation stack.");

            return stack.Pop();
        }

        private static int PopIlInt(Stack<object?> stack, string name)
        {
            object? value = PopIlValue(stack, name);
            if (value is int intValue)
                return intValue;

            throw new InvalidDataException($"{name}: expected integer on IL evaluation stack, got {value ?? "nil"}.");
        }

        private static bool IsLoadArgument(IlInstruction instruction, int argumentIndex)
        {
            if (argumentIndex == 0 && instruction.OpCode == OpCodes.Ldarg_0)
                return true;
            if (argumentIndex == 1 && instruction.OpCode == OpCodes.Ldarg_1)
                return true;
            if (argumentIndex == 2 && instruction.OpCode == OpCodes.Ldarg_2)
                return true;
            if (argumentIndex == 3 && instruction.OpCode == OpCodes.Ldarg_3)
                return true;

            return (instruction.OpCode == OpCodes.Ldarg || instruction.OpCode == OpCodes.Ldarg_S)
                && instruction.Operand is int operandArgumentIndex
                && operandArgumentIndex == argumentIndex;
        }

        private static int? LoadLocalIndex(IlInstruction instruction)
        {
            if (instruction.OpCode == OpCodes.Ldloc_0)
                return 0;
            if (instruction.OpCode == OpCodes.Ldloc_1)
                return 1;
            if (instruction.OpCode == OpCodes.Ldloc_2)
                return 2;
            if (instruction.OpCode == OpCodes.Ldloc_3)
                return 3;
            if ((instruction.OpCode == OpCodes.Ldloc || instruction.OpCode == OpCodes.Ldloc_S) && instruction.Operand is int localIndex)
                return localIndex;

            return null;
        }

        private static int? StoreLocalIndex(IlInstruction instruction)
        {
            if (instruction.OpCode == OpCodes.Stloc_0)
                return 0;
            if (instruction.OpCode == OpCodes.Stloc_1)
                return 1;
            if (instruction.OpCode == OpCodes.Stloc_2)
                return 2;
            if (instruction.OpCode == OpCodes.Stloc_3)
                return 3;
            if ((instruction.OpCode == OpCodes.Stloc || instruction.OpCode == OpCodes.Stloc_S) && instruction.Operand is int localIndex)
                return localIndex;

            return null;
        }

        private static int FindIlInstructionIndexByOffset(IReadOnlyList<IlInstruction> instructions, int offset, string name)
        {
            for (int index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].Offset == offset)
                    return index;
            }

            throw new InvalidDataException($"{name}: expected IL target offset {offset} to resolve to an instruction.");
        }

        private static void AssertNormalCharacterRewardRetailRates(MethodInfo drawCharacterReward, string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(drawCharacterReward).ToList();
            const int rollUpperBound = 9860;
            int[] cumulativeThresholds = [50, 1445, 3656, 6495, 7937, 8418];
            MethodInfo randomNextInt = RequiredMethod(
                typeof(Random),
                nameof(Random.Next),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int)]);
            bool usesRetailRollRange = instructions
                .Select((instruction, index) => (instruction, index))
                .Any(candidate => LdcI4Value(candidate.instruction) == rollUpperBound
                    && instructions
                        .Skip(candidate.index + 1)
                        .Take(8)
                        .Any(instruction => instruction.Operand is MethodBase calledMethod
                            && MethodsMatch(calledMethod, randomNextInt)));
            if (!usesRetailRollRange)
                throw new InvalidDataException($"{name}: expected DrawCharacterReward to roll Random.Shared.Next(9860).");
            AssertMethodContainsOrderedIntConstants(drawCharacterReward, instructions, cumulativeThresholds, name);

            List<long> branchWeights = [];
            int previousThreshold = 0;
            foreach (int threshold in cumulativeThresholds.Append(rollUpperBound))
            {
                branchWeights.Add(threshold - previousThreshold);
                previousThreshold = threshold;
            }
            AssertIntegerList([50, 1395, 2211, 2839, 1442, 481, 1442], branchWeights, $"{name} branch weights");
        }

        private static void AssertLegacyCharacterRewardRetailRates(MethodInfo drawLegacyCharacterReward, string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(drawLegacyCharacterReward).ToList();
            MethodInfo randomNextDouble = RequiredMethod(
                typeof(Random),
                nameof(Random.NextDouble),
                BindingFlags.Instance | BindingFlags.Public,
                Type.EmptyTypes);
            if (FindCallIndex(instructions, randomNextDouble, startIndex: 0) < 0)
                throw new InvalidDataException($"{name}: expected DrawLegacyCharacterReward to roll Random.Shared.NextDouble().");
            AssertMethodContainsOrderedDoubleConstants(drawLegacyCharacterReward, instructions, [0.015, 0.25, 0.58], name);
        }

        private static void AssertEquipRewardRetailRates(MethodInfo drawEquipReward, MethodInfo drawPreviewEquipReward, MethodInfo drawRandomWeaponReward, string name)
        {
            List<IlInstruction> instructions = ReadIlInstructions(drawEquipReward).ToList();
            const int rollUpperBound = 10000;
            int[] cumulativeThresholds = [400, 450, 600, 750, 4090, 6880, 7815, 8750];
            MethodInfo randomNextInt = RequiredMethod(
                typeof(Random),
                nameof(Random.Next),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int)]);
            MethodInfo randomSharedGetter = typeof(Random).GetProperty(nameof(Random.Shared), BindingFlags.Static | BindingFlags.Public)?.GetMethod
                ?? throw new MissingMemberException(typeof(Random).FullName, nameof(Random.Shared));
            bool usesRetailRollRange = instructions
                .Select((instruction, index) => (instruction, index))
                .Any(candidate =>
                {
                    if (candidate.instruction.Operand is not MethodBase calledMethod || !MethodsMatch(calledMethod, randomNextInt))
                        return false;

                    int windowStart = Math.Max(0, candidate.index - 8);
                    return InstructionWindowHasConstant(instructions, rollUpperBound, candidate.index, maxInstructionsBack: 8)
                        && instructions
                            .Skip(windowStart)
                            .Take(candidate.index - windowStart)
                            .Any(instruction => instruction.Operand is MethodBase sharedMethod && MethodsMatch(sharedMethod, randomSharedGetter));
                });
            if (!usesRetailRollRange)
                throw new InvalidDataException($"{name}: expected DrawEquipReward to roll Random.Shared.Next(10000).");
            AssertMethodContainsOrderedIntConstants(drawEquipReward, instructions, cumulativeThresholds, name);

            List<long> branchWeights = [];
            int previousThreshold = 0;
            foreach (int threshold in cumulativeThresholds.Append(rollUpperBound))
            {
                branchWeights.Add(threshold - previousThreshold);
                previousThreshold = threshold;
            }
            AssertIntegerList([400, 50, 150, 150, 3340, 2790, 935, 935, 1250], branchWeights.ToArray(), $"{name} branch weights");

            int previewEquipRewardIndex = FindCallIndex(instructions, drawPreviewEquipReward, startIndex: 0);
            if (previewEquipRewardIndex < 0)
                throw new InvalidDataException($"{name}: expected DrawEquipReward low five-star branch to call DrawPreviewEquipReward.");
            int previewFallbackIndex = FindCallIndex(instructions, drawRandomWeaponReward, previewEquipRewardIndex + 1);
            if (previewFallbackIndex < 0 || !InstructionWindowHasConstant(instructions, 5, previewFallbackIndex, maxInstructionsBack: 10))
                throw new InvalidDataException($"{name}: expected DrawPreviewEquipReward nil safety fallback to DrawRandomWeaponReward(quality: 5).");
        }


        private static void AssertMethodContainsOrderedIntConstants(MethodInfo method, IReadOnlyList<IlInstruction> instructions, IReadOnlyList<int> expectedConstants, string name)
        {
            int lastConstantIndex = -1;
            foreach (int expectedConstant in expectedConstants)
            {
                int constantIndex = instructions.ToList().FindIndex(lastConstantIndex + 1, instruction => LdcI4Value(instruction) == expectedConstant);
                if (constantIndex < 0)
                    throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} IL to contain ordered constant {expectedConstant}.");
                lastConstantIndex = constantIndex;
            }
        }

        private static void AssertMethodContainsOrderedDoubleConstants(MethodInfo method, IReadOnlyList<IlInstruction> instructions, IReadOnlyList<double> expectedConstants, string name)
        {
            int lastConstantIndex = -1;
            foreach (double expectedConstant in expectedConstants)
            {
                int constantIndex = instructions.ToList().FindIndex(lastConstantIndex + 1, instruction =>
                    LdcR8Value(instruction) is double value && Math.Abs(value - expectedConstant) < 0.0000000001);
                if (constantIndex < 0)
                    throw new InvalidDataException($"{name}: expected {method.DeclaringType?.FullName}.{method.Name} IL to contain ordered constant {expectedConstant}.");
                lastConstantIndex = constantIndex;
            }
        }

        private static void AssertDraw1488RetailRewardPool(DrawInfo eventConstruct1488, long drawPityPlayerId, int weaponResearchGroupId, int targetWeaponResearchGroupId, int cubTargetGroupId)
        {
            AssertEqual(false, eventConstruct1488.GroupId == weaponResearchGroupId || eventConstruct1488.GroupId == targetWeaponResearchGroupId || eventConstruct1488.GroupId == cubTargetGroupId, "Draw 1488 retail-like reward routing uses construct reward pool");
            AssertTargetCharacterPityDraw(drawPityPlayerId, eventConstruct1488, expectedTargetCharacterId: 1021007, targetName: "Lucia: Inverse Crown");

            const int targetCharacterId = 1021007;
            const int excludedVeraGeiravorCharacterId = 1131004;
            const int targetShardItemId = 592;
            const string targetShardItemName = "Inver-Shard - Inverse Crown";
            const int excludedVeraGeiravorShardItemId = 581;
            const string excludedVeraGeiravorShardItemName = "Inver-Shard - Geiravor";
            List<CharacterTable> characterRows = TableReaderV2.Parse<CharacterTable>();
            Dictionary<int, CharacterTable> characterRowsById = characterRows.ToDictionary(character => character.Id);
            Dictionary<int, ItemTable> itemRowsById = TableReaderV2.Parse<ItemTable>().ToDictionary(item => item.Id);
            CharacterTable targetCharacter = characterRowsById.TryGetValue(targetCharacterId, out CharacterTable? targetCharacterRow)
                ? targetCharacterRow
                : throw new InvalidDataException("Draw 1488 Lucia: Inverse Crown Character.tsv: expected character row 1021007.");
            AssertEqual(targetShardItemId, targetCharacter.ItemId, "Draw 1488 Lucia: Inverse Crown Character.tsv ItemId");
            ItemTable targetShardItem = itemRowsById.TryGetValue(targetShardItemId, out ItemTable? targetShardItemRow)
                ? targetShardItemRow
                : throw new InvalidDataException("Draw 1488 Lucia: Inverse Crown shard Item.tsv: expected item row 592.");
            AssertEqual(targetShardItemName, targetShardItem.Name, "Draw 1488 Lucia: Inverse Crown Item.tsv shard name");
            CharacterTable excludedVeraGeiravorCharacter = characterRowsById.TryGetValue(excludedVeraGeiravorCharacterId, out CharacterTable? excludedVeraGeiravorCharacterRow)
                ? excludedVeraGeiravorCharacterRow
                : throw new InvalidDataException("Draw 1488 Vera: Geiravor Character.tsv: expected character row 1131004.");
            AssertEqual(excludedVeraGeiravorShardItemId, excludedVeraGeiravorCharacter.ItemId, "Draw 1488 Vera: Geiravor Character.tsv ItemId");
            ItemTable excludedVeraGeiravorShardItem = itemRowsById.TryGetValue(excludedVeraGeiravorShardItemId, out ItemTable? excludedVeraGeiravorShardItemRow)
                ? excludedVeraGeiravorShardItemRow
                : throw new InvalidDataException("Draw 1488 Vera: Geiravor shard Item.tsv: expected item row 581.");
            AssertEqual(excludedVeraGeiravorShardItemName, excludedVeraGeiravorShardItem.Name, "Draw 1488 Vera: Geiravor Item.tsv shard name");
            int configuredTargetCharacterId = eventConstruct1488.ResourceIds.TryGetValue(1, out int resourceId)
                ? resourceId
                : throw new InvalidDataException("Draw 1488 ResourceIds: expected target character id in slot 1.");
            AssertEqual(targetCharacterId, configuredTargetCharacterId, "Draw 1488 configured target character id");

            DrawPreviewTable preview = TableReaderV2.Parse<DrawPreviewTable>().SingleOrDefault(preview => preview.Id == eventConstruct1488.Id)
                ?? throw new InvalidDataException("Draw 1488 preview shard pool: expected DrawPreview row 1488.");
            int[] previewCharacterIds = preview.UpGoodsId
                .Concat(preview.GoodsId)
                .Where(characterRowsById.ContainsKey)
                .Distinct()
                .ToArray();
            AssertEqual(true, previewCharacterIds.Contains(targetCharacterId), "Draw 1488 preview/config includes Lucia: Inverse Crown character 1021007");
            AssertEqual(false, previewCharacterIds.Contains(excludedVeraGeiravorCharacterId), "Draw 1488 preview/config excludes Vera: Geiravor character 1131004");

            int[] previewShardCharacterIds = preview.GoodsId
                .Where(characterId => characterRowsById.TryGetValue(characterId, out CharacterTable? character)
                    && AscNet.Common.Database.Inventory.IsValidClientItemId(character.ItemId))
                .Distinct()
                .ToArray();
            int[] previewShardItemIds = previewShardCharacterIds
                .Select(characterId => characterRowsById[characterId].ItemId)
                .Distinct()
                .ToArray();
            if (previewShardItemIds.Length == 0)
                throw new InvalidDataException("Draw 1488 preview-derived shard pool: expected at least one valid DrawPreview.GoodsId character shard ItemId.");
            int[] offBannerShardItemIds = previewShardCharacterIds
                .Where(characterId => characterId != targetCharacterId)
                .Select(characterId => characterRowsById[characterId].ItemId)
                .Distinct()
                .ToArray();
            if (offBannerShardItemIds.Length == 0)
                throw new InvalidDataException("Draw 1488 preview-derived shard pool: expected at least one valid off-banner DrawPreview.GoodsId shard ItemId.");
            AssertEqual(false, previewShardCharacterIds.Contains(excludedVeraGeiravorCharacterId), "Draw 1488 preview-derived shard pool excludes Vera: Geiravor character 1131004");
            AssertEqual(false, previewShardItemIds.Contains(excludedVeraGeiravorShardItemId), "Draw 1488 preview-derived shard pool excludes Vera: Geiravor shard item 581");
            foreach (int shardItemId in previewShardItemIds)
            {
                if (!itemRowsById.TryGetValue(shardItemId, out ItemTable? shardItem))
                    throw new InvalidDataException($"Draw 1488 preview-derived shard pool: expected local Inver-Shard item {shardItemId}.");
                if (!shardItem.Name.StartsWith("Inver-Shard", StringComparison.Ordinal))
                    throw new InvalidDataException($"Draw 1488 preview-derived shard pool item {shardItem.Id}: expected Inver-Shard item name, got '{shardItem.Name}'.");
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
                (drawCharacterShardReward, "character shard item"),
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
            int firstShardRewardIndex = FindCallIndex(drawCharacterRewardInstructions, drawCharacterShardReward, startIndex: 0);
            if (!InstructionWindowHasConstant(drawCharacterRewardInstructions, 1445, firstShardRewardIndex, maxInstructionsBack: 8))
                throw new InvalidDataException("Draw 1488 retail-like reward routing: expected low-tier roll branch to return DrawCharacterShardReward.");
            int secondShardRewardIndex = FindCallIndex(drawCharacterRewardInstructions, drawCharacterShardReward, firstShardRewardIndex + 1);
            if (secondShardRewardIndex < 0 || !InstructionWindowHasConstant(drawCharacterRewardInstructions, 3656, secondShardRewardIndex, maxInstructionsBack: 8))
                throw new InvalidDataException("Draw 1488 retail-like reward routing: expected shard roll branch to return DrawCharacterShardReward after the low-tier shard branch.");
            if (FindCallIndex(drawCharacterRewardInstructions, drawRandomWeaponReward, startIndex: 0) >= 0)
                throw new InvalidDataException("Draw 1488 retail-like reward routing: expected construct reward pool not to reach off-rate weapon branch.");
            const int draw1488RollUpperBound = 9860;
            int[] draw1488CumulativeThresholds = [50, 1445, 3656, 6495, 7937, 8418];
            MethodInfo randomNextInt = RequiredMethod(
                typeof(Random),
                nameof(Random.Next),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(int)]);
            bool usesScreenshotRollRange = drawCharacterRewardInstructions
                .Select((instruction, index) => (instruction, index))
                .Any(candidate => LdcI4Value(candidate.instruction) == draw1488RollUpperBound
                    && drawCharacterRewardInstructions
                        .Skip(candidate.index + 1)
                        .Take(8)
                        .Any(instruction => instruction.Operand is MethodBase calledMethod
                            && MethodsMatch(calledMethod, randomNextInt)));
            if (!usesScreenshotRollRange)
                throw new InvalidDataException("Draw 1488 retail-like construct reward thresholds: expected DrawCharacterReward to roll Random.Shared.Next(9860).");
            AssertMethodContainsIntConstants(
                drawCharacterReward,
                drawCharacterRewardInstructions,
                draw1488CumulativeThresholds,
                "Draw 1488 retail-like construct reward cumulative thresholds");
            int lastThresholdInstructionIndex = -1;
            foreach (int threshold in draw1488CumulativeThresholds)
            {
                int thresholdInstructionIndex = drawCharacterRewardInstructions.FindIndex(lastThresholdInstructionIndex + 1, instruction => LdcI4Value(instruction) == threshold);
                if (thresholdInstructionIndex < 0)
                    throw new InvalidDataException($"Draw 1488 retail-like construct reward thresholds: expected cumulative threshold {threshold} after the previous branch threshold.");
                lastThresholdInstructionIndex = thresholdInstructionIndex;
            }
            List<long> draw1488BranchWeights = [];
            int previousThreshold = 0;
            foreach (int threshold in draw1488CumulativeThresholds.Append(draw1488RollUpperBound))
            {
                draw1488BranchWeights.Add(threshold - previousThreshold);
                previousThreshold = threshold;
            }
            AssertIntegerList(
                [50, 1395, 2211, 2839, 1442, 481, 1442],
                draw1488BranchWeights.ToArray(),
                "Draw 1488 retail-like construct reward branch weights");
            List<IlInstruction> shardInstructions = ReadIlInstructions(drawCharacterShardReward).ToList();
            List<IlInstruction> memoryInstructions = ReadIlInstructions(drawMemoryReward).ToList();
            FieldInfo drawPreviewTablesField = drawManagerType.GetField("drawPreviewTables", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingFieldException(drawManagerType.FullName, "drawPreviewTables");
            FieldInfo equipTablesField = drawManagerType.GetField("equipTables", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(drawManagerType.FullName, "equipTables");
            Type drawWaferShowTableType = typeof(DrawPreviewTable).Assembly.GetType("AscNet.Table.V2.client.draw.DrawWaferShowTable", throwOnError: true)
                ?? throw new TypeLoadException("AscNet.Table.V2.client.draw.DrawWaferShowTable");
            FieldInfo drawWaferShowIdsField = drawManagerType.GetField("drawWaferShowIds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new MissingFieldException(drawManagerType.FullName, "drawWaferShowIds");
            if (drawWaferShowIdsField.FieldType != typeof(HashSet<int>))
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory ids: expected DrawManager.drawWaferShowIds to be a static HashSet<int>.");
            if (drawManagerType.GetField("retailFourStarMemoryIds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) is not null)
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory pool: expected DrawManager not to define a hardcoded retailFourStarMemoryIds allowlist.");
            if (drawManagerType.GetField("drawSceneModelIds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) is not null)
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory pool: expected DrawMemoryReward to use DrawWaferShow rows, not DrawScene.ModelId rows.");
            MethodInfo drawPreviewGoodsIdGetter = typeof(DrawPreviewTable).GetProperty(nameof(DrawPreviewTable.GoodsId), BindingFlags.Instance | BindingFlags.Public)?.GetMethod
                ?? throw new MissingMemberException(typeof(DrawPreviewTable).FullName, nameof(DrawPreviewTable.GoodsId));
            if (!shardInstructions.Any(instruction =>
                    instruction.Operand is FieldInfo loadedField
                    && FieldsMatch(loadedField, drawPreviewTablesField)))
                throw new InvalidDataException("Draw 1488 preview-derived shard pool: expected DrawCharacterShardReward to load DrawManager.drawPreviewTables.");
            if (FindCallIndex(shardInstructions, drawPreviewGoodsIdGetter, startIndex: 0) < 0)
                throw new InvalidDataException("Draw 1488 preview-derived shard pool: expected DrawCharacterShardReward to read DrawPreview.GoodsId before falling back to the target shard.");
            AssertMethodContainsIntConstants(drawCharacterShardReward, shardInstructions, [2, 6, 18], "Draw 1488 Inver-Shard reward counts");
            MethodInfo[] drawMemoryRewardHelpers = drawManagerType.GetNestedTypes(BindingFlags.NonPublic)
                .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                .Where(method => method.Name.Contains("DrawMemoryReward", StringComparison.Ordinal))
                .ToArray();
            List<List<IlInstruction>> memoryInstructionScopes = [memoryInstructions];
            memoryInstructionScopes.AddRange(drawMemoryRewardHelpers.Select(method => ReadIlInstructions(method).ToList()));
            PropertyInfo? equipTypeProperty = typeof(EquipTable).GetProperty(nameof(EquipTable.Type), BindingFlags.Instance | BindingFlags.Public);
            FieldInfo? equipTypeField = typeof(EquipTable).GetField(nameof(EquipTable.Type), BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo? equipQualityProperty = typeof(EquipTable).GetProperty(nameof(EquipTable.Quality), BindingFlags.Instance | BindingFlags.Public);
            FieldInfo? equipQualityField = typeof(EquipTable).GetField(nameof(EquipTable.Quality), BindingFlags.Instance | BindingFlags.Public);
            MethodInfo? equipTypeGetter = equipTypeProperty?.GetMethod;
            MethodInfo? equipQualityGetter = equipQualityProperty?.GetMethod;
            MethodInfo isOwnableEquipTemplate = RequiredMethod(
                typeof(AscNet.Common.Database.Character),
                nameof(AscNet.Common.Database.Character.IsOwnableEquipTemplate),
                BindingFlags.Static | BindingFlags.Public,
                [typeof(EquipTable)]);
            if (equipTypeGetter is null && equipTypeField is null)
                throw new MissingMemberException(typeof(EquipTable).FullName, nameof(EquipTable.Type));
            if (equipQualityGetter is null && equipQualityField is null)
                throw new MissingMemberException(typeof(EquipTable).FullName, nameof(EquipTable.Quality));
            bool ReadsEquipMember(List<IlInstruction> scope, MethodInfo? getter, FieldInfo? field)
            {
                return scope.Any(instruction =>
                    (getter is not null && instruction.Operand is MethodBase calledGetter && MethodsMatch(calledGetter, getter))
                    || (field is not null && instruction.Operand is FieldInfo loadedField && FieldsMatch(loadedField, field)));
            }
            bool IsIntContainsAllowlistCall(MethodBase calledMethod)
            {
                if (!string.Equals(calledMethod.Name, nameof(HashSet<int>.Contains), StringComparison.Ordinal))
                    return false;
                if (calledMethod is MethodInfo { IsGenericMethod: true } genericMethod
                    && genericMethod.GetGenericArguments().Any(argument => argument == typeof(int)))
                    return true;
                return calledMethod.GetParameters().Any(parameter => parameter.ParameterType == typeof(int));
            }
            if (!memoryInstructions.Any(instruction =>
                    instruction.Operand is FieldInfo loadedField
                    && FieldsMatch(loadedField, equipTablesField)))
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory pool: expected DrawMemoryReward to load DrawManager.equipTables.");
            if (!memoryInstructionScopes.Any(scope => ReadsEquipMember(scope, equipTypeGetter, equipTypeField) && scope.Any(instruction => LdcI4Value(instruction) == 0)))
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory pool: expected DrawMemoryReward to filter EquipTable.Type == 0.");
            if (!memoryInstructionScopes.Any(scope => ReadsEquipMember(scope, equipQualityGetter, equipQualityField) && scope.Any(instruction => LdcI4Value(instruction) == 4)))
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory pool: expected DrawMemoryReward to filter EquipTable.Quality == 4.");
            if (!memoryInstructionScopes.Any(scope => FindCallIndex(scope, isOwnableEquipTemplate, startIndex: 0) >= 0))
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory pool: expected DrawMemoryReward to filter through Character.IsOwnableEquipTemplate.");
            if (!memoryInstructionScopes.Any(scope =>
                    scope.Any(instruction => instruction.Operand is FieldInfo loadedField && FieldsMatch(loadedField, drawWaferShowIdsField))
                    && scope.Any(instruction => instruction.Operand is MethodBase calledMethod && IsIntContainsAllowlistCall(calledMethod))))
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory pool: expected DrawMemoryReward to filter ownable Type=0 Quality=4 memories through DrawManager.drawWaferShowIds.");
            FieldInfo[] unexpectedDrawManagerStaticFields = memoryInstructionScopes
                .SelectMany(scope => scope)
                .Select(instruction => instruction.Operand as FieldInfo)
                .Where(field => field is not null
                    && field.DeclaringType == drawManagerType
                    && field.IsStatic
                    && !FieldsMatch(field, equipTablesField)
                    && !FieldsMatch(field, drawWaferShowIdsField))
                .Cast<FieldInfo>()
                .GroupBy(field => (field.Module, field.MetadataToken))
                .Select(group => group.First())
                .ToArray();
            if (unexpectedDrawManagerStaticFields.Length > 0)
                throw new InvalidDataException($"Draw 1488 client-renderable DrawWaferShow memory pool: expected DrawMemoryReward to load only equipTables plus the drawWaferShowIds renderability filter; loaded {string.Join(", ", unexpectedDrawManagerStaticFields.Select(field => field.Name))}.");

            if (RequiredGenericMethodDefinition(typeof(TableReaderV2), nameof(TableReaderV2.Parse), BindingFlags.Public | BindingFlags.Static, parameterCount: 0)
                    .MakeGenericMethod(drawWaferShowTableType)
                    .Invoke(null, null) is not System.Collections.IEnumerable drawWaferShowRows)
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory ids: expected DrawWaferShow.tsv to parse as an enumerable table pool.");
            HashSet<int> drawWaferShowIds = drawWaferShowIdsField.GetValue(null) as HashSet<int>
                ?? throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory ids: expected DrawManager.drawWaferShowIds to be an initialized HashSet<int>.");
            int[] drawWaferShowTableIds = drawWaferShowRows
                .Cast<object>()
                .Select(row => RequiredIntegerRowMember(row, "Id", "Draw 1488 client-renderable DrawWaferShow memory ids DrawWaferShow.tsv Id"))
                .Where(id => id > 0)
                .Distinct()
                .Order()
                .ToArray();
            AssertIntegerList(
                drawWaferShowTableIds.Select(memoryId => (long)memoryId).ToArray(),
                drawWaferShowIds.Order().Select(memoryId => (long)memoryId).ToArray(),
                "Draw 1488 client-renderable DrawWaferShow memory ids configured from DrawWaferShow.tsv");

            List<EquipTable> equipRows = TableReaderV2.Parse<EquipTable>();
            int[] expectedDynamicMemoryEquipIds = equipRows
                .Where(equip => equip.Type == 0
                    && equip.Quality == 4
                    && AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip))
                .Select(equip => equip.Id)
                .Order()
                .ToArray();
            if (expectedDynamicMemoryEquipIds.Length == 0)
                throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory pool: expected current Equip.tsv to contain ownable Type=0 Quality=4 memory rows.");
            int[] expectedRenderableMemoryEquipIds = expectedDynamicMemoryEquipIds
                .Where(drawWaferShowIds.Contains)
                .Order()
                .ToArray();
            IEnumerable<EquipTable> drawManagerEquipRows = equipTablesField.GetValue(null) as IEnumerable<EquipTable>
                ?? throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory pool: expected DrawManager.equipTables to be an enumerable EquipTable pool.");
            int[] configuredRenderableMemoryEquipIds = drawManagerEquipRows
                .Where(equip => equip.Type == 0
                    && equip.Quality == 4
                    && AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip)
                    && drawWaferShowIds.Contains(equip.Id))
                .Select(equip => equip.Id)
                .Order()
                .ToArray();
            AssertIntegerList(
                expectedRenderableMemoryEquipIds.Select(memoryId => (long)memoryId).ToArray(),
                configuredRenderableMemoryEquipIds.Select(memoryId => (long)memoryId).ToArray(),
                "Draw 1488 client-renderable DrawWaferShow memory ids equal Equip.tsv Type=0 Quality=4 ownable intersection");

            int[] originalDrawWaferShowIds = drawWaferShowIds.ToArray();
            try
            {
                drawWaferShowIds.Clear();
                RewardGoods fallbackReward = drawMemoryReward.Invoke(null, null) as RewardGoods
                    ?? throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory ids empty-pool fallback item behavior: expected DrawMemoryReward to return a fallback reward, got nil.");
                AssertEqual((int)RewardType.Item, fallbackReward.RewardType, "Draw 1488 client-renderable DrawWaferShow memory ids empty-pool fallback item behavior reward type");
                if (fallbackReward.TemplateId <= 0)
                    throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory ids empty-pool fallback item behavior: expected fallback item TemplateId to be positive.");
                if (fallbackReward.Count <= 0)
                    throw new InvalidDataException("Draw 1488 client-renderable DrawWaferShow memory ids empty-pool fallback item behavior: expected fallback item Count to be positive.");
            }
            finally
            {
                drawWaferShowIds.Clear();
                foreach (int memoryId in originalDrawWaferShowIds)
                    drawWaferShowIds.Add(memoryId);
            }

            static int RequiredIntegerRowMember(object row, string memberName, string name)
            {
                Type rowType = row.GetType();
                object? value = rowType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(row)
                    ?? rowType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(row);
                if (value is null)
                    throw new InvalidDataException($"{name}: expected {rowType.FullName}.{memberName}.");
                return Convert.ToInt32(value);
            }
        }

        private static void AssertCurrentCharacterBannerDrawPreviewRows()
        {
            List<DrawPreviewTable> previewRows = TableReaderV2.Parse<DrawPreviewTable>();
            DrawPreviewTable baselinePreview = previewRows.SingleOrDefault(preview => preview.Id == 1488)
                ?? throw new InvalidDataException("Current character banner DrawPreview rows: expected baseline DrawPreview row 1488.");
            Dictionary<int, CharacterTable> characterRowsById = TableReaderV2.Parse<CharacterTable>().ToDictionary(character => character.Id);
            Dictionary<int, ItemTable> itemRowsById = TableReaderV2.Parse<ItemTable>().ToDictionary(item => item.Id);
            long[] baselineGoodsIds = baselinePreview.GoodsId.Select(goodsId => (long)goodsId).ToArray();
            long[] keyRetailOffBannerCharacterIds = [1301002, 1241002, 1081003, 1131002, 1021001, 1031001, 1051001];
            (int DrawId, int TargetCharacterId)[] expectedPreviewTargets =
            [
                (1492, 1291003),
                (1493, 1381003),
                (1494, 1171004),
                (1498, 1031005),
                (2482, 1021007),
                (2486, 1291003),
                (2487, 1381003),
                (2488, 1171004),
                (2492, 1031005)
            ];

            foreach ((int drawId, int expectedTargetCharacterId) in expectedPreviewTargets)
            {
                DrawPreviewTable preview = previewRows.SingleOrDefault(preview => preview.Id == drawId)
                    ?? throw new InvalidDataException($"Current character banner DrawPreview row {drawId}: expected DrawPreview.tsv row.");

                AssertEqual(true, preview.UpGoodsId.Contains(expectedTargetCharacterId), $"Current character banner DrawPreview row {drawId} UpGoodsId contains target character {expectedTargetCharacterId}");
                AssertIntegerList(
                    baselineGoodsIds,
                    preview.GoodsId.Select(goodsId => (long)goodsId).ToArray(),
                    $"Current character banner DrawPreview row {drawId} GoodsId matches row 1488 shard pool order");
                AssertIntegerSetContainsAll(
                    keyRetailOffBannerCharacterIds,
                    preview.GoodsId.Select(goodsId => (long)goodsId).ToArray(),
                    $"Current character banner DrawPreview row {drawId} GoodsId key retail off-banner character pool");

                int[] validOffBannerShardItemIds = preview.GoodsId
                    .Where(characterId => characterId != expectedTargetCharacterId)
                    .Select(characterId => characterRowsById.TryGetValue(characterId, out CharacterTable? character) ? character.ItemId : 0)
                    .Where(AscNet.Common.Database.Inventory.IsValidClientItemId)
                    .Where(itemId => itemRowsById.TryGetValue(itemId, out ItemTable? item)
                        && item.Name.StartsWith("Inver-Shard", StringComparison.Ordinal))
                    .Distinct()
                    .ToArray();
                if (validOffBannerShardItemIds.Length == 0)
                    throw new InvalidDataException($"Current character banner DrawPreview row {drawId}: expected GoodsId to contain at least one valid off-banner character with an Inver-Shard item id so shard rewards cannot fall back to target-only.");
            }
        }

        private static void AssertDraw1488DuplicatePityTenDraw(DrawInfo eventConstruct1488)
        {
            const long playerId = 880010;
            const int packetId = 8811;
            const int drawId = 1488;
            const int targetCharacterId = 1021007;
            const int expectedDuplicateShardItemId = 592;
            const string expectedDuplicateShardName = "Inver-Shard - Inverse Crown";
            int[] genericShardRecycleMaterialIds =
            [
                AscNet.Common.Database.Inventory.SClassInverMaterial,
                AscNet.Common.Database.Inventory.AClassInverMaterial,
                AscNet.Common.Database.Inventory.SRankUniframeMaterial
            ];
            const int excludedVeraGeiravorCharacterId = 1131004;
            const int excludedVeraGeiravorShardItemId = 581;
            const string excludedVeraGeiravorShardItemName = "Inver-Shard - Geiravor";
            const int targetDrawTicketItemId = 50005;
            const int drawCount = 10;
            const int desiredBottomTimesBeforeTenDraw = 7;
            const string name = "Draw 1488 BottomTimes=7 duplicate pity 10x";

            AssertEqual(drawId, eventConstruct1488.Id, $"{name} DrawInfo Id");
            AssertEqual(targetDrawTicketItemId, eventConstruct1488.UseItemId, $"{name} DrawInfo UseItemId");
            AssertEqual(60, eventConstruct1488.MaxBottomTimes, $"{name} DrawInfo MaxBottomTimes");
            AssertEqual(47, eventConstruct1488.BottomTimes, $"{name} DrawInfo BottomTimes");
            if (!eventConstruct1488.ResourceIds.TryGetValue(1, out int configuredTargetCharacterId))
                throw new InvalidDataException($"{name}: expected ResourceIds[1] target character id.");
            AssertEqual(targetCharacterId, configuredTargetCharacterId, $"{name} configured target character id");

            Dictionary<int, CharacterTable> characterRowsById = TableReaderV2.Parse<CharacterTable>().ToDictionary(character => character.Id);
            CharacterTable targetCharacter = characterRowsById[targetCharacterId];
            int duplicateShardItemId = targetCharacter.ItemId;
            AssertEqual(expectedDuplicateShardItemId, duplicateShardItemId, $"{name} Lucia: Inverse Crown Character.tsv ItemId");
            if (!AscNet.Common.Database.Inventory.IsValidClientItemId(duplicateShardItemId))
                throw new InvalidDataException($"{name}: Character.tsv duplicate shard ItemId {duplicateShardItemId} is not a valid client item.");
            CharacterTable excludedVeraGeiravorCharacter = characterRowsById[excludedVeraGeiravorCharacterId];
            AssertEqual(excludedVeraGeiravorShardItemId, excludedVeraGeiravorCharacter.ItemId, $"{name} Vera: Geiravor Character.tsv ItemId");
            Dictionary<int, ItemTable> itemRowsById = TableReaderV2.Parse<ItemTable>().ToDictionary(item => item.Id);
            ItemTable duplicateShardItem = itemRowsById[duplicateShardItemId];
            AssertEqual(expectedDuplicateShardName, duplicateShardItem.Name, $"{name} duplicate shard item name");
            ItemTable excludedVeraGeiravorShardItem = itemRowsById[excludedVeraGeiravorShardItemId];
            AssertEqual(excludedVeraGeiravorShardItemName, excludedVeraGeiravorShardItem.Name, $"{name} Vera: Geiravor shard item name");

            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.AddCharacter((uint)targetCharacterId);
            DrawInfo preloadedDrawInfo = PreloadDrawProgressToBottomTimes(playerId, eventConstruct1488, desiredBottomTimesBeforeTenDraw, name);
            int forcedRewardIndex = preloadedDrawInfo.BottomTimes - 1;
            AssertEqual(6, forcedRewardIndex, $"{name} forced duplicate reward index from table BottomTimes");

            DrawDrawCardRequest drawCardRequest = new()
            {
                DrawId = drawId,
                Count = drawCount,
                UseDrawTicketId = 0
            };
            DrawDrawCardRequest drawCardRequestRoundTrip = MessagePackSerializer.Deserialize<DrawDrawCardRequest>(
                MessagePackSerializer.Serialize(drawCardRequest));
            AssertEqual(drawId, drawCardRequestRoundTrip.DrawId, $"{name} DrawDrawCardRequest DrawId MessagePack round-trip");
            AssertEqual(drawCount, drawCardRequestRoundTrip.Count, $"{name} DrawDrawCardRequest Count MessagePack round-trip");
            AssertEqual(0, drawCardRequestRoundTrip.UseDrawTicketId, $"{name} DrawDrawCardRequest UseDrawTicketId MessagePack round-trip");

            DrawDrawCardResponse? drawCardResponse = null;
            List<NotifyItemDataList> itemPushes = [];
            using (LoopbackSessionHarness harness = new(
                character,
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, [new Item { Id = targetDrawTicketItemId, Count = eventConstruct1488.UseItemCount * drawCount }]),
                "draw-1488-duplicate-pity-compat-test"))
            {
                InvokeRegisteredRequestHandler("DrawDrawCardRequest", harness.Session, packetId, drawCardRequestRoundTrip);

                for (int packetIndex = 0; packetIndex < 8; packetIndex++)
                {
                    Packet packet = harness.ReadPacket($"{name} packet {packetIndex + 1}");
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        if (push.Name == nameof(NotifyItemDataList))
                            itemPushes.Add(MessagePackSerializer.Deserialize<NotifyItemDataList>(push.Content));
                        continue;
                    }

                    AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} response packet type");
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(packetId, response.Id, $"{name} response packet id");
                    AssertEqual(nameof(DrawDrawCardResponse), response.Name, $"{name} response packet name");
                    drawCardResponse = MessagePackSerializer.Deserialize<DrawDrawCardResponse>(response.Content);
                    break;
                }
            }

            if (drawCardResponse is null)
                throw new InvalidDataException($"{name}: expected DrawDrawCardResponse after draw reward pushes.");
            AssertEqual(0, drawCardResponse.Code, $"{name} Code");

            Item[] notifiedItems = itemPushes.SelectMany(push => push.ItemDataList).ToArray();
            if (notifiedItems.Length == 0)
                throw new InvalidDataException($"{name}: expected NotifyItemDataList for ticket cost and duplicate compensation.");
            if (notifiedItems.Any(item => item.Id == 0))
                throw new InvalidDataException($"{name}: NotifyItemDataList must not contain unknown client item id 0.");

            Item ticketItem = notifiedItems.Single(item => item.Id == targetDrawTicketItemId);
            AssertEqual(0L, ticketItem.Count, $"{name} NotifyItemDataList consumed all draw tickets");

            long duplicateShardCount = notifiedItems
                .Where(item => item.Id == duplicateShardItemId)
                .Sum(item => item.Count);
            long excludedVeraGeiravorShardCount = notifiedItems
                .Where(item => item.Id == excludedVeraGeiravorShardItemId)
                .Sum(item => item.Count);
            if (excludedVeraGeiravorShardCount > 0)
                throw new InvalidDataException($"{name}: must not grant Vera: Geiravor shard item {excludedVeraGeiravorShardItemId} as duplicate compensation for Lucia: Inverse Crown, got {excludedVeraGeiravorShardCount}.");
            Item[] forbiddenNotifiedItems = notifiedItems
                .Where(item => genericShardRecycleMaterialIds.Contains(item.Id))
                .ToArray();
            if (forbiddenNotifiedItems.Length > 0)
            {
                string forbiddenItemSummary = string.Join(
                    ", ",
                    forbiddenNotifiedItems.Select(item => $"{item.Id} count {item.Count}"));
                throw new InvalidDataException($"{name}: NotifyItemDataList must not grant generic shard recycle materials [{string.Join(", ", genericShardRecycleMaterialIds)}] as duplicate compensation for Lucia: Inverse Crown shard item {duplicateShardItemId}; got {forbiddenItemSummary}.");
            }
            if (duplicateShardCount <= 0)
                throw new InvalidDataException($"{name}: expected NotifyItemDataList to grant positive Lucia: Inverse Crown shard item {duplicateShardItemId}, got {duplicateShardCount}.");

            AssertEqual(drawCount, drawCardResponse.RewardGoodsList.Count, $"{name} RewardGoodsList count");
            List<RewardGoods> forbiddenRewardGoods = drawCardResponse.RewardGoodsList
                .Where(rewardGoods => rewardGoods.RewardType == (int)RewardType.Item
                    && genericShardRecycleMaterialIds.Contains(rewardGoods.TemplateId))
                .ToList();
            if (forbiddenRewardGoods.Count > 0)
            {
                string forbiddenRewardSummary = string.Join(
                    ", ",
                    forbiddenRewardGoods.Select(rewardGoods => $"{rewardGoods.TemplateId} count {rewardGoods.Count}"));
                throw new InvalidDataException($"{name}: RewardGoodsList must not grant generic shard recycle materials [{string.Join(", ", genericShardRecycleMaterialIds)}] as duplicate compensation for Lucia: Inverse Crown shard item {duplicateShardItemId}; got {forbiddenRewardSummary}.");
            }
            RewardGoods forcedDuplicateReward = drawCardResponse.RewardGoodsList[forcedRewardIndex];
            AssertEqual((int)RewardType.Item, forcedDuplicateReward.RewardType, $"{name} forced pity duplicate reward type");
            AssertEqual(duplicateShardItemId, forcedDuplicateReward.TemplateId, $"{name} forced pity duplicate shard item id");
            if (forcedDuplicateReward.Count <= 0)
                throw new InvalidDataException($"{name}: expected positive forced pity duplicate shard count for item {duplicateShardItemId}, got {forcedDuplicateReward.Count}.");
            AssertConvertFromItemRewardShowQuality(
                forcedDuplicateReward,
                duplicateShardItemId,
                targetCharacterId,
                expectedSourceCharacterShowQuality: 3,
                expectedShardItemQuality: 4,
                expectedRewardQuality: 0,
                expectedRewardConvertFrom: targetCharacterId,
                expectedRewardShowQuality: 0,
                name: $"{name} forced pity duplicate ConvertFrom RewardGoods");

            DrawInfo clientDrawInfo = drawCardResponse.ClientDrawInfo
                ?? throw new InvalidDataException($"{name}: expected ClientDrawInfo after draw.");
            AssertEqual(preloadedDrawInfo.TotalCount + drawCount, clientDrawInfo.TotalCount, $"{name} ClientDrawInfo TotalCount after 10x draw");
            int expectedClientBottomTimes = preloadedDrawInfo.BottomTimes - drawCount;
            if (expectedClientBottomTimes <= 0)
                expectedClientBottomTimes += eventConstruct1488.MaxBottomTimes;
            AssertEqual(expectedClientBottomTimes, clientDrawInfo.BottomTimes, $"{name} ClientDrawInfo BottomTimes after crossing pity");
        }

        private static void AssertDraw1488RepresentativeRewardGoodsShowQualityHydrated()
        {
            const int targetCharacterId = 1021007;
            const int duplicateShardItemId = 592;
            const int targetCharacterShowQuality = 3;
            const int lowRankDuplicateCharacterId = 1211002;
            const int lowRankDuplicateShardItemId = 543;
            const int lowRankDuplicateCharacterShowQuality = 2;
            const int duplicateShardItemQuality = 4;
            const string name = "Draw 1488 RewardGoods ShowQuality hydration";

            Type drawModuleType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.DrawModule");
            MethodInfo toDrawRewardGoods = RequiredMethod(
                drawModuleType,
                "ToDrawRewardGoods",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Reward)]);
            Type drawManagerType = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
            MethodInfo drawOverclockMaterialReward = RequiredMethod(
                drawManagerType,
                "DrawOverclockMaterialReward",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo drawMemoryReward = RequiredMethod(
                drawManagerType,
                "DrawMemoryReward",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo drawExpMaterialReward = RequiredMethod(
                drawManagerType,
                "DrawExpMaterialReward",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo drawCogBoxReward = RequiredMethod(
                drawManagerType,
                "DrawCogBoxReward",
                BindingFlags.Static | BindingFlags.NonPublic);
            Type drawInfoTemplateType = drawManagerType.GetNestedType("DrawInfoTemplate", BindingFlags.NonPublic)
                ?? throw new MissingMemberException(drawManagerType.FullName, "DrawInfoTemplate");
            MethodInfo drawCharacterShardReward = RequiredMethod(
                drawManagerType,
                "DrawCharacterShardReward",
                BindingFlags.Static | BindingFlags.NonPublic,
                [drawInfoTemplateType]);
            object draw1488Template = RequiredRetailDrawInfoTemplate(drawManagerType, drawInfoTemplateType, 1488, name);

            DrawPreviewTable draw1488Preview = TableReaderV2.Parse<DrawPreviewTable>()
                .SingleOrDefault(preview => preview.Id == 1488)
                ?? throw new InvalidDataException($"{name}: expected DrawPreview.tsv row 1488.");
            AssertEqual(true, draw1488Preview.GoodsId.Contains(lowRankDuplicateCharacterId), $"{name} low-rank duplicate fixture is in draw 1488 preview GoodsId");

            AssertDrawOverclockMaterialRewardUsesHighGradeMaterialPool(
                drawOverclockMaterialReward,
                toDrawRewardGoods,
                $"{name} DrawManager.DrawOverclockMaterialReward");

            RewardGoods normalEquipSource = InvokeRewardGoodsFactory(drawMemoryReward, $"{name} normal equip source");
            RewardGoods normalEquipReward = InvokeToDrawRewardGoods(toDrawRewardGoods, normalEquipSource, $"{name} normal equip");
            AssertEqual((int)RewardType.Equip, normalEquipReward.RewardType, $"{name} normal equip RewardType");
            AssertRewardGoodsDrawShowQuality(normalEquipReward, $"{name} normal equip", requirePositive: true);

            RewardGoods lowRankDuplicateConvertFromReward = InvokeToDrawRewardGoods(
                toDrawRewardGoods,
                new Reward
                {
                    Id = lowRankDuplicateShardItemId,
                    Count = 6,
                    Type = RewardType.Item,
                    ConvertFrom = lowRankDuplicateCharacterId
                },
                $"{name} low-rank duplicate ConvertFrom");
            AssertConvertFromItemRewardShowQuality(
                lowRankDuplicateConvertFromReward,
                lowRankDuplicateShardItemId,
                lowRankDuplicateCharacterId,
                expectedSourceCharacterShowQuality: lowRankDuplicateCharacterShowQuality,
                expectedShardItemQuality: duplicateShardItemQuality,
                expectedRewardQuality: 0,
                expectedRewardConvertFrom: lowRankDuplicateCharacterId,
                expectedRewardShowQuality: 0,
                name: $"{name} low-rank duplicate ConvertFrom");

            RewardGoods duplicateConvertFromReward = InvokeToDrawRewardGoods(
                toDrawRewardGoods,
                new Reward
                {
                    Id = duplicateShardItemId,
                    Count = 18,
                    Type = RewardType.Item,
                    ConvertFrom = targetCharacterId
                },
                $"{name} duplicate ConvertFrom");
            AssertConvertFromItemRewardShowQuality(
                duplicateConvertFromReward,
                duplicateShardItemId,
                targetCharacterId,
                expectedSourceCharacterShowQuality: targetCharacterShowQuality,
                expectedShardItemQuality: duplicateShardItemQuality,
                expectedRewardQuality: 0,
                expectedRewardConvertFrom: targetCharacterId,
                expectedRewardShowQuality: 0,
                name: $"{name} duplicate ConvertFrom");

            AssertDrawFillerItemRewardFactoryExcludesUnsupportedClientRingQualities(
                drawExpMaterialReward,
                toDrawRewardGoods,
                [30011, 31101, 31102, 31201, 31202],
                $"{name} DrawManager.DrawExpMaterialReward");
            AssertDrawFillerItemRewardFactoryExcludesUnsupportedClientRingQualities(
                drawCogBoxReward,
                toDrawRewardGoods,
                [90011],
                $"{name} DrawManager.DrawCogBoxReward");

            AssertDrawCharacterShardRewardExcludesUnsupportedClientRingQualities(
                drawCharacterShardReward,
                toDrawRewardGoods,
                draw1488Template,
                [1021001, lowRankDuplicateCharacterId],
                $"{name} DrawManager low-tier DrawCharacterShardReward branch");
        }

        private static void AssertDrawFillerItemRewardFactoryExcludesUnsupportedClientRingQualities(
            MethodInfo factory,
            MethodInfo toDrawRewardGoods,
            int[] unsupportedItemIds,
            string name)
        {
            Dictionary<int, ItemTable> itemRowsById = TableReaderV2.Parse<ItemTable>().ToDictionary(item => item.Id);
            Type drawManagerType = factory.DeclaringType
                ?? throw new InvalidDataException($"{name}: expected {factory.Name} to have a declaring type.");
            FieldInfo itemTablesField = drawManagerType.GetField("itemTables", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(drawManagerType.FullName, "itemTables");
            List<ItemTable> itemTables = itemTablesField.GetValue(null) as List<ItemTable>
                ?? throw new InvalidDataException($"{name}: expected DrawManager.itemTables to be an initialized List<ItemTable>.");

            ItemTable[] originalItemTables = itemTables.ToArray();
            try
            {
                foreach (int unsupportedItemId in unsupportedItemIds)
                {
                    ItemTable unsupportedItem = itemRowsById.TryGetValue(unsupportedItemId, out ItemTable? itemRow)
                        ? itemRow
                        : throw new InvalidDataException($"{name}: expected Item.tsv row {unsupportedItemId}.");
                    if (unsupportedItem.Quality is not (1 or 2))
                        throw new InvalidDataException($"{name}: expected {unsupportedItem.Name} Item.tsv quality fixture to remain 1 or 2, got {unsupportedItem.Quality}.");

                    itemTables.Clear();
                    itemTables.Add(unsupportedItem);

                    RewardGoods? unsupportedReward = factory.Invoke(null, null) as RewardGoods;
                    if (unsupportedReward is null)
                        continue;

                    AssertDrawFillerItemRewardSupportedClientRingQuality(
                        unsupportedReward,
                        toDrawRewardGoods,
                        itemRowsById,
                        $"{name} single-row unsupported candidate {unsupportedItem.Id} {unsupportedItem.Name}");
                }

                itemTables.Clear();
                itemTables.AddRange(originalItemTables);

                RewardGoods representativeReward = InvokeRewardGoodsFactory(factory, $"{name} representative supported source");
                AssertDrawFillerItemRewardSupportedClientRingQuality(
                    representativeReward,
                    toDrawRewardGoods,
                    itemRowsById,
                    $"{name} representative supported source");
            }
            finally
            {
                itemTables.Clear();
                itemTables.AddRange(originalItemTables);
            }
        }

        private static void AssertDrawFillerItemRewardSupportedClientRingQuality(
            RewardGoods reward,
            MethodInfo toDrawRewardGoods,
            IReadOnlyDictionary<int, ItemTable> itemRowsById,
            string name)
        {
            AssertEqual((int)RewardType.Item, reward.RewardType, $"{name} factory RewardGoods reward type");
            AssertRewardGoodsDrawShowQuality(reward, $"{name} factory RewardGoods", requirePositive: false);

            ItemTable item = itemRowsById.TryGetValue(reward.TemplateId, out ItemTable? itemRow)
                ? itemRow
                : throw new InvalidDataException($"{name}: expected Item.tsv row {reward.TemplateId}.");
            AssertDrawFillerItemQualitySupportedClientRing(item, $"{name} Item.tsv row {item.Id} {item.Name}");

            RewardGoods clientReward = InvokeToDrawRewardGoods(toDrawRewardGoods, reward, $"{name} client RewardGoods");
            AssertEqual((int)RewardType.Item, clientReward.RewardType, $"{name} client RewardGoods reward type");
            AssertEqual(reward.TemplateId, clientReward.TemplateId, $"{name} client RewardGoods TemplateId");
            int showQuality = AssertRewardGoodsDrawShowQuality(clientReward, $"{name} client RewardGoods", requirePositive: true);
            AssertEqual(item.Quality, showQuality, $"{name} client RewardGoods ShowQuality matches Item.tsv Quality");
        }

        private static void AssertDrawOverclockMaterialRewardUsesHighGradeMaterialPool(
            MethodInfo factory,
            MethodInfo toDrawRewardGoods,
            string name)
        {
            int[] highGradeMaterialItemIds = [40_110, 40_111, 40_112, 40_113, 40_114];
            int[] unopenedBoxItemIds = [60_001, 60_002];
            int[] lowGradeMaterialItemIds = [40_100, 40_101, 40_102, 40_103, 40_104];
            Dictionary<int, ItemTable> itemRowsById = TableReaderV2.Parse<ItemTable>().ToDictionary(item => item.Id);

            RewardGoods sourceReward = InvokeRewardGoodsFactory(factory, $"{name} factory RewardGoods");
            AssertEqual((int)RewardType.Item, sourceReward.RewardType, $"{name} factory RewardGoods reward type");
            AssertEqual(true, highGradeMaterialItemIds.Contains(sourceReward.TemplateId), $"{name} factory RewardGoods TemplateId is a high-grade Overclock Material");
            AssertEqual(false, unopenedBoxItemIds.Contains(sourceReward.TemplateId), $"{name} factory RewardGoods TemplateId is not an unopened Overclock Material box");
            AssertEqual(1, sourceReward.Count, $"{name} factory RewardGoods Count");
            AssertEqual(false, sourceReward.IsGift, $"{name} factory RewardGoods IsGift");

            ItemTable materialItem = itemRowsById.TryGetValue(sourceReward.TemplateId, out ItemTable? itemRow)
                ? itemRow
                : throw new InvalidDataException($"{name}: expected Item.tsv row {sourceReward.TemplateId}.");
            AssertDrawFillerItemQualitySupportedClientRing(materialItem, $"{name} Item.tsv row {materialItem.Id} {materialItem.Name}");

            RewardGoods clientReward = InvokeToDrawRewardGoods(toDrawRewardGoods, sourceReward, $"{name} client RewardGoods");
            AssertEqual(sourceReward.RewardType, clientReward.RewardType, $"{name} client RewardGoods preserves RewardType");
            AssertEqual(sourceReward.TemplateId, clientReward.TemplateId, $"{name} client RewardGoods preserves material TemplateId");
            AssertEqual(true, highGradeMaterialItemIds.Contains(clientReward.TemplateId), $"{name} client RewardGoods TemplateId is a high-grade Overclock Material");
            AssertEqual(false, unopenedBoxItemIds.Contains(clientReward.TemplateId), $"{name} client RewardGoods TemplateId is not an unopened Overclock Material box");
            AssertEqual(false, clientReward.IsGift, $"{name} client RewardGoods IsGift remains false for direct Overclock Material");
            AssertEqual(1, clientReward.Count, $"{name} client RewardGoods Count");
            AssertEqual(sourceReward.Count, clientReward.Count, $"{name} client RewardGoods preserves Count");
            int showQuality = AssertRewardGoodsDrawShowQuality(clientReward, $"{name} client RewardGoods", requirePositive: true);
            AssertEqual(materialItem.Quality, showQuality, $"{name} client RewardGoods ShowQuality matches material Item.tsv Quality");

            AssertDrawOverclockMaterialRewardAcceptsSingleMaterialRows(
                factory,
                toDrawRewardGoods,
                highGradeMaterialItemIds,
                itemRowsById,
                $"{name} high-grade material pool");
            AssertDrawOverclockMaterialRewardRejectsItemRows(
                factory,
                unopenedBoxItemIds,
                itemRowsById,
                $"{name} unopened box exclusion");
            AssertDrawOverclockMaterialRewardRejectsItemRows(
                factory,
                lowGradeMaterialItemIds,
                itemRowsById,
                $"{name} low-grade material exclusion");
        }

        private static void AssertDrawOverclockMaterialRewardAcceptsSingleMaterialRows(
            MethodInfo factory,
            MethodInfo toDrawRewardGoods,
            int[] materialItemIds,
            IReadOnlyDictionary<int, ItemTable> itemRowsById,
            string name)
        {
            Type drawManagerType = factory.DeclaringType
                ?? throw new InvalidDataException($"{name}: expected {factory.Name} to have a declaring type.");
            FieldInfo itemTablesField = drawManagerType.GetField("itemTables", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(drawManagerType.FullName, "itemTables");
            List<ItemTable> itemTables = itemTablesField.GetValue(null) as List<ItemTable>
                ?? throw new InvalidDataException($"{name}: expected DrawManager.itemTables to be an initialized List<ItemTable>.");

            ItemTable[] originalItemTables = itemTables.ToArray();
            try
            {
                foreach (int materialItemId in materialItemIds)
                {
                    ItemTable materialItem = itemRowsById.TryGetValue(materialItemId, out ItemTable? itemRow)
                        ? itemRow
                        : throw new InvalidDataException($"{name}: expected Item.tsv row {materialItemId}.");
                    AssertDrawFillerItemQualitySupportedClientRing(materialItem, $"{name} Item.tsv row {materialItem.Id} {materialItem.Name}");

                    itemTables.Clear();
                    itemTables.Add(materialItem);

                    RewardGoods materialReward = InvokeRewardGoodsFactory(factory, $"{name} single-row material {materialItem.Id} {materialItem.Name}");
                    AssertEqual((int)RewardType.Item, materialReward.RewardType, $"{name} single-row material {materialItem.Id} RewardType");
                    AssertEqual(materialItem.Id, materialReward.TemplateId, $"{name} single-row material {materialItem.Id} TemplateId");
                    AssertEqual(1, materialReward.Count, $"{name} single-row material {materialItem.Id} Count");
                    AssertEqual(false, materialReward.IsGift, $"{name} single-row material {materialItem.Id} IsGift");

                    RewardGoods clientReward = InvokeToDrawRewardGoods(toDrawRewardGoods, materialReward, $"{name} single-row material {materialItem.Id} client RewardGoods");
                    AssertEqual((int)RewardType.Item, clientReward.RewardType, $"{name} single-row material {materialItem.Id} client RewardType");
                    AssertEqual(materialItem.Id, clientReward.TemplateId, $"{name} single-row material {materialItem.Id} client TemplateId");
                    AssertEqual(1, clientReward.Count, $"{name} single-row material {materialItem.Id} client Count");
                    AssertEqual(false, clientReward.IsGift, $"{name} single-row material {materialItem.Id} client IsGift");
                    int showQuality = AssertRewardGoodsDrawShowQuality(clientReward, $"{name} single-row material {materialItem.Id} client RewardGoods", requirePositive: true);
                    AssertEqual(materialItem.Quality, showQuality, $"{name} single-row material {materialItem.Id} client ShowQuality matches Item.tsv Quality");
                }
            }
            finally
            {
                itemTables.Clear();
                itemTables.AddRange(originalItemTables);
            }
        }

        private static void AssertDrawOverclockMaterialRewardRejectsItemRows(
            MethodInfo factory,
            int[] rejectedItemIds,
            IReadOnlyDictionary<int, ItemTable> itemRowsById,
            string name)
        {
            Type drawManagerType = factory.DeclaringType
                ?? throw new InvalidDataException($"{name}: expected {factory.Name} to have a declaring type.");
            FieldInfo itemTablesField = drawManagerType.GetField("itemTables", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(drawManagerType.FullName, "itemTables");
            List<ItemTable> itemTables = itemTablesField.GetValue(null) as List<ItemTable>
                ?? throw new InvalidDataException($"{name}: expected DrawManager.itemTables to be an initialized List<ItemTable>.");

            ItemTable[] originalItemTables = itemTables.ToArray();
            try
            {
                foreach (int rejectedItemId in rejectedItemIds)
                {
                    ItemTable rejectedItem = itemRowsById.TryGetValue(rejectedItemId, out ItemTable? itemRow)
                        ? itemRow
                        : throw new InvalidDataException($"{name}: expected Item.tsv row {rejectedItemId}.");

                    itemTables.Clear();
                    itemTables.Add(rejectedItem);

                    RewardGoods? rejectedReward = factory.Invoke(null, null) as RewardGoods;
                    if (rejectedReward is not null)
                        throw new InvalidDataException($"{name} {rejectedItem.Id} {rejectedItem.Name}: expected DrawOverclockMaterialReward to ignore this row and return no reward, got TemplateId {rejectedReward.TemplateId}, Count {rejectedReward.Count}, RewardType {rejectedReward.RewardType}.");
                }
            }
            finally
            {
                itemTables.Clear();
                itemTables.AddRange(originalItemTables);
            }
        }


        private static void AssertDrawFillerItemQualitySupportedClientRing(ItemTable item, string name)
        {
            if (item.Quality is 1 or 2)
                throw new InvalidDataException($"{name}: Item.tsv Quality {item.Quality} maps to missing client DrawRingQualityEffect{item.Quality}; expected DrawManager filler item rewards to choose quality 3/4/5/6 items.");
            if (item.Quality is not (3 or 4 or 5 or 6))
                throw new InvalidDataException($"{name}: expected Item.tsv Quality to be one of the client-supported positive draw ring indices 3/4/5/6, got {item.Quality}.");
        }

        private static void AssertDrawCharacterShardRewardExcludesUnsupportedClientRingQualities(
            MethodInfo factory,
            MethodInfo toDrawRewardGoods,
            object drawInfoTemplate,
            int[] unsupportedCharacterIds,
            string name)
        {
            Dictionary<int, CharacterTable> characterRowsById = TableReaderV2.Parse<CharacterTable>().ToDictionary(character => character.Id);
            Dictionary<int, ItemTable> itemRowsById = TableReaderV2.Parse<ItemTable>().ToDictionary(item => item.Id);
            Type drawManagerType = factory.DeclaringType
                ?? throw new InvalidDataException($"{name}: expected {factory.Name} to have a declaring type.");
            FieldInfo charactersTablesField = drawManagerType.GetField("charactersTables", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingFieldException(drawManagerType.FullName, "charactersTables");
            List<CharacterTable> characterTables = charactersTablesField.GetValue(null) as List<CharacterTable>
                ?? throw new InvalidDataException($"{name}: expected DrawManager.charactersTables to be an initialized List<CharacterTable>.");

            CharacterTable[] originalCharacterTables = characterTables.ToArray();
            try
            {
                foreach (int unsupportedCharacterId in unsupportedCharacterIds)
                {
                    CharacterTable unsupportedCharacter = characterRowsById.TryGetValue(unsupportedCharacterId, out CharacterTable? characterRow)
                        ? characterRow
                        : throw new InvalidDataException($"{name}: expected Character.tsv row {unsupportedCharacterId}.");
                    int unsupportedCharacterQuality = GetFirstCharacterQuality(unsupportedCharacter.Id, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name}");
                    if (unsupportedCharacterQuality is not (1 or 2))
                        throw new InvalidDataException($"{name}: expected {unsupportedCharacter.Name} CharacterQuality.tsv minimum fixture to remain 1 or 2, got {unsupportedCharacterQuality}.");
                    if (!AscNet.Common.Database.Inventory.IsValidClientItemId(unsupportedCharacter.ItemId))
                        throw new InvalidDataException($"{name}: expected {unsupportedCharacter.Name} Character.tsv ItemId {unsupportedCharacter.ItemId} to be a valid shard item.");
                    ItemTable shardItem = itemRowsById.TryGetValue(unsupportedCharacter.ItemId, out ItemTable? itemRow)
                        ? itemRow
                        : throw new InvalidDataException($"{name}: expected Item.tsv shard row {unsupportedCharacter.ItemId}.");

                    characterTables.Clear();
                    characterTables.Add(unsupportedCharacter);

                    RewardGoods shardReward = factory.Invoke(null, [drawInfoTemplate]) as RewardGoods
                        ?? throw new InvalidDataException($"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name}: expected DrawCharacterShardReward to return a shard item reward.");
                    AssertEqual((int)RewardType.Item, shardReward.RewardType, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name} factory RewardGoods reward type");
                    AssertEqual(unsupportedCharacter.ItemId, shardReward.TemplateId, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name} factory RewardGoods shard item id");
                    if (shardReward.Count <= 0)
                        throw new InvalidDataException($"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name}: expected positive shard item count, got {shardReward.Count}.");

                    RewardGoods clientReward = InvokeToDrawRewardGoods(toDrawRewardGoods, shardReward, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name} client RewardGoods");
                    AssertEqual((int)RewardType.Item, clientReward.RewardType, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name} client RewardGoods reward type");
                    AssertEqual(unsupportedCharacter.ItemId, clientReward.TemplateId, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name} client RewardGoods shard item id");
                    AssertEqual(0, clientReward.ConvertFrom, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name} client RewardGoods ConvertFrom");
                    AssertEqual(shardItem.Quality, clientReward.Quality, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name} client RewardGoods Quality matches Item.tsv Quality");
                    int showQuality = AssertRewardGoodsDrawShowQuality(clientReward, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name} client RewardGoods", requirePositive: true);
                    AssertEqual(shardItem.Quality, showQuality, $"{name} single-row unsupported candidate {unsupportedCharacter.Id} {unsupportedCharacter.Name} client RewardGoods ShowQuality matches Item.tsv Quality");
                }
            }
            finally
            {
                characterTables.Clear();
                characterTables.AddRange(originalCharacterTables);
            }
        }

        private static object RequiredRetailDrawInfoTemplate(Type drawManagerType, Type drawInfoTemplateType, int drawId, string name)
        {
            FieldInfo retailDrawInfoByIdField = drawManagerType.GetField("RetailDrawInfoById", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(drawManagerType.FullName, "RetailDrawInfoById");
            if (retailDrawInfoByIdField.GetValue(null) is not System.Collections.IDictionary retailDrawInfoById)
                throw new InvalidDataException($"{name}: expected DrawManager.RetailDrawInfoById to be an initialized dictionary.");
            object drawInfoTemplate = retailDrawInfoById[drawId]
                ?? throw new InvalidDataException($"{name}: expected DrawManager.RetailDrawInfoById to contain draw {drawId}.");
            if (!drawInfoTemplateType.IsInstanceOfType(drawInfoTemplate))
                throw new InvalidDataException($"{name}: expected draw {drawId} template to be a {drawInfoTemplateType.FullName}.");
            return drawInfoTemplate;
        }


        private static void AssertConvertFromItemRewardShowQuality(
            RewardGoods reward,
            int expectedShardItemId,
            int sourceCharacterId,
            int expectedSourceCharacterShowQuality,
            int expectedShardItemQuality,
            int expectedRewardQuality,
            int expectedRewardConvertFrom,
            int expectedRewardShowQuality,
            string name)
        {
            AssertEqual((int)RewardType.Item, reward.RewardType, $"{name} RewardType");
            AssertEqual(expectedShardItemId, reward.TemplateId, $"{name} shard item id");
            AssertEqual(expectedRewardConvertFrom, reward.ConvertFrom, $"{name} RewardGoods ConvertFrom presentation id");

            CharacterTable sourceCharacter = TableReaderV2.Parse<CharacterTable>()
                .SingleOrDefault(character => character.Id == sourceCharacterId)
                ?? throw new InvalidDataException($"{name}: expected Character.tsv row {sourceCharacterId}.");
            AssertEqual(expectedShardItemId, sourceCharacter.ItemId, $"{name} Character.tsv source ItemId");

            ItemTable shardItem = TableReaderV2.Parse<ItemTable>()
                .SingleOrDefault(item => item.Id == expectedShardItemId)
                ?? throw new InvalidDataException($"{name}: expected Item.tsv shard row {expectedShardItemId}.");
            AssertEqual(expectedShardItemQuality, shardItem.Quality, $"{name} Item.tsv shard Quality regression fixture");
            int sourceCharacterQuality = GetFirstCharacterQuality(sourceCharacterId, name);
            AssertEqual(expectedSourceCharacterShowQuality, sourceCharacterQuality, $"{name} ConvertFrom CharacterQuality minimum regression fixture");
            if (shardItem.Quality == sourceCharacterQuality)
                throw new InvalidDataException($"{name}: expected fixture shard Item.tsv Quality to differ from source CharacterQuality minimum so the assertion detects item-quality regressions.");

            AssertEqual(expectedRewardQuality, reward.Quality, $"{name} RewardGoods Quality");
            int showQuality = expectedRewardShowQuality > 0
                ? AssertRewardGoodsDrawShowQuality(reward, name, requirePositive: true)
                : GetRewardGoodsDrawShowQuality(reward, name);
            AssertEqual(expectedRewardShowQuality, showQuality, $"{name} RewardGoods ShowQuality");
        }

        private static int GetFirstCharacterQuality(int characterId, string name)
        {
            return TableReaderV2.Parse<CharacterQualityTable>()
                .Where(quality => quality.CharacterId == characterId)
                .OrderBy(quality => quality.Quality)
                .FirstOrDefault()?.Quality
                ?? throw new InvalidDataException($"{name}: expected CharacterQuality.tsv rows for character {characterId}.");
        }

        private static RewardGoods InvokeRewardGoodsFactory(MethodInfo factory, string name)
        {
            return factory.Invoke(null, null) as RewardGoods
                ?? throw new InvalidDataException($"{name}: expected {factory.Name} to return RewardGoods.");
        }

        private static RewardGoods InvokeToDrawRewardGoods(MethodInfo toDrawRewardGoods, RewardGoods source, string name)
        {
            return InvokeToDrawRewardGoods(
                toDrawRewardGoods,
                new Reward
                {
                    Id = source.TemplateId,
                    Count = source.Count,
                    Level = source.Level,
                    Type = (RewardType)source.RewardType,
                    ConvertFrom = source.ConvertFrom
                },
                name);
        }

        private static RewardGoods InvokeToDrawRewardGoods(MethodInfo toDrawRewardGoods, Reward source, string name)
        {
            return toDrawRewardGoods.Invoke(null, [source]) as RewardGoods
                ?? throw new InvalidDataException($"{name}: expected DrawModule.ToDrawRewardGoods to return RewardGoods.");
        }

        private static int AssertRewardGoodsDrawShowQuality(RewardGoods reward, string name, bool requirePositive)
        {
            int showQuality = GetRewardGoodsDrawShowQuality(reward, name);

            if (showQuality is 1 or 2)
                throw new InvalidDataException($"{name}: RewardGoods.ShowQuality {showQuality} maps to missing client DrawRingQualityEffect{showQuality}; expected a supported ring index 0/3/4/5/6.");
            if (showQuality is not (0 or 3 or 4 or 5 or 6))
                throw new InvalidDataException($"{name}: expected RewardGoods.ShowQuality to be one of the client-supported draw ring indices 0/3/4/5/6, got {showQuality}.");
            if (requirePositive && showQuality <= 0)
                throw new InvalidDataException($"{name}: expected positive RewardGoods.ShowQuality for a draw reward rendered with a ring, got {showQuality}.");

            return showQuality;
        }

        private static int GetRewardGoodsDrawShowQuality(RewardGoods reward, string name)
        {
            object? showQualityValue;
            PropertyInfo? showQualityProperty = typeof(RewardGoods).GetProperty("ShowQuality", BindingFlags.Instance | BindingFlags.Public);
            if (showQualityProperty is not null)
            {
                showQualityValue = showQualityProperty.GetValue(reward);
            }
            else
            {
                FieldInfo? showQualityField = typeof(RewardGoods).GetField("ShowQuality", BindingFlags.Instance | BindingFlags.Public);
                if (showQualityField is null)
                    throw new MissingMemberException(typeof(RewardGoods).FullName, "ShowQuality");
                showQualityValue = showQualityField.GetValue(reward);
            }

            if (showQualityValue is null)
                throw new InvalidDataException($"{name}: expected RewardGoods.ShowQuality to be populated.");

            try
            {
                return Convert.ToInt32(showQualityValue);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidDataException($"{name}: expected RewardGoods.ShowQuality to be an integer value, got {showQualityValue}.", ex);
            }
        }

        private static void AssertDrawDrawCardHandlerRejectsInvalidOrUnaffordableRequests(DrawInfo drawInfo)
        {
            const int packetId = 8814;
            const long invalidDrawPlayerId = 880013;
            const long invalidTicketPlayerId = 880014;
            const long insufficientCostPlayerId = 880015;
            const int invalidDrawId = 9_999_999;
            const int invalidTicketId = 9_999_998;
            const string name = "DrawDrawCardRequest invalid cost rejection";

            AssertRejected(
                invalidDrawPlayerId,
                new DrawDrawCardRequest
                {
                    DrawId = invalidDrawId,
                    Count = 1,
                    UseDrawTicketId = 0
                },
                CreateDrawCompatibilityInventory(
                    invalidDrawPlayerId,
                    [new Item { Id = drawInfo.UseItemId, Count = drawInfo.UseItemCount }]),
                drawInfo.UseItemId,
                drawInfo.UseItemCount,
                $"{name} invalid draw id");

            AssertRejected(
                invalidTicketPlayerId,
                new DrawDrawCardRequest
                {
                    DrawId = drawInfo.Id,
                    Count = 1,
                    UseDrawTicketId = invalidTicketId
                },
                CreateDrawCompatibilityInventory(
                    invalidTicketPlayerId,
                    [new Item { Id = drawInfo.UseItemId, Count = drawInfo.UseItemCount }]),
                drawInfo.UseItemId,
                drawInfo.UseItemCount,
                $"{name} invalid UseDrawTicketId");

            long insufficientCount = Math.Max(0, drawInfo.UseItemCount - 1L);
            AssertRejected(
                insufficientCostPlayerId,
                new DrawDrawCardRequest
                {
                    DrawId = drawInfo.Id,
                    Count = 1,
                    UseDrawTicketId = 0
                },
                CreateDrawCompatibilityInventory(
                    insufficientCostPlayerId,
                    [new Item { Id = drawInfo.UseItemId, Count = insufficientCount }]),
                drawInfo.UseItemId,
                insufficientCount,
                $"{name} insufficient draw tickets");

            void AssertRejected(
                long playerId,
                DrawDrawCardRequest request,
                AscNet.Common.Database.Inventory inventory,
                int expectedItemId,
                long expectedItemCount,
                string caseName)
            {
                DrawDrawCardRequest roundTrip = MessagePackSerializer.Deserialize<DrawDrawCardRequest>(
                    MessagePackSerializer.Serialize(request));
                using LoopbackSessionHarness harness = new(
                    CreateDrawCompatibilityCharacter(playerId),
                    CreateDrawCompatibilityPlayer(playerId),
                    inventory,
                    caseName);

                InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, packetId, roundTrip);
                DrawDrawCardResponse response = ReadResponsePayload<DrawDrawCardResponse>(
                    harness,
                    packetId,
                    nameof(DrawDrawCardResponse),
                    $"{caseName} response");

                AssertEqual(1, response.Code, $"{caseName} Code");
                AssertEmptyList(response.RewardGoodsList, $"{caseName} RewardGoodsList");
                if (response.ClientDrawInfo is not null)
                    throw new InvalidDataException($"{caseName}: expected nil ClientDrawInfo on rejected draw.");
                if (harness.TryReadAvailablePacket($"{caseName} unexpected packet", out Packet unexpectedPacket))
                    throw new InvalidDataException($"{caseName}: rejected draw must not send reward pushes, got {unexpectedPacket.Type}.");

                long actualItemCount = inventory.Items.FirstOrDefault(item => item.Id == expectedItemId)?.Count ?? 0;
                AssertEqual(expectedItemCount, actualItemCount, $"{caseName} inventory item {expectedItemId} unchanged");
            }
        }

        private static void AssertDraw1488TargetCharacterRewardGoodsHydrated(DrawInfo eventConstruct1488)
        {
            const long playerId = 880011;
            const int packetId = 8812;
            const int drawId = 1488;
            const int targetCharacterId = 1021007;
            const int targetDrawTicketItemId = 50005;
            const string name = "Draw 1488 forced target character RewardGoods hydration";

            AssertEqual(drawId, eventConstruct1488.Id, $"{name} DrawInfo Id");
            AssertEqual(targetDrawTicketItemId, eventConstruct1488.UseItemId, $"{name} DrawInfo UseItemId");
            DrawInfo preloadedDrawInfo = PreloadDrawProgressToBottomTimes(playerId, eventConstruct1488, desiredBottomTimes: 1, name);

            DrawDrawCardRequest drawCardRequest = new()
            {
                DrawId = drawId,
                Count = 1,
                UseDrawTicketId = 0
            };
            DrawDrawCardRequest drawCardRequestRoundTrip = MessagePackSerializer.Deserialize<DrawDrawCardRequest>(
                MessagePackSerializer.Serialize(drawCardRequest));
            AssertEqual(drawId, drawCardRequestRoundTrip.DrawId, $"{name} DrawDrawCardRequest DrawId MessagePack round-trip");
            AssertEqual(1, drawCardRequestRoundTrip.Count, $"{name} DrawDrawCardRequest Count MessagePack round-trip");
            AssertEqual(0, drawCardRequestRoundTrip.UseDrawTicketId, $"{name} DrawDrawCardRequest UseDrawTicketId MessagePack round-trip");

            DrawDrawCardResponse? drawCardResponse = null;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, [new Item { Id = targetDrawTicketItemId, Count = eventConstruct1488.UseItemCount }]),
                "draw-1488-target-character-reward-goods-hydration-test"))
            {
                InvokeRegisteredRequestHandler("DrawDrawCardRequest", harness.Session, packetId, drawCardRequestRoundTrip);

                for (int packetIndex = 0; packetIndex < 8; packetIndex++)
                {
                    Packet packet = harness.ReadPacket($"{name} packet {packetIndex + 1}");
                    if (packet.Type == Packet.ContentType.Push)
                        continue;

                    AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} response packet type");
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(packetId, response.Id, $"{name} response packet id");
                    AssertEqual(nameof(DrawDrawCardResponse), response.Name, $"{name} response packet name");
                    drawCardResponse = MessagePackSerializer.Deserialize<DrawDrawCardResponse>(response.Content);
                    break;
                }
            }

            if (drawCardResponse is null)
                throw new InvalidDataException($"{name}: expected DrawDrawCardResponse after draw reward pushes.");

            AssertEqual(1, drawCardResponse.RewardGoodsList.Count, $"{name} RewardGoodsList count");
            AssertCharacterRewardGoodsHydrated(drawCardResponse.RewardGoodsList[0], targetCharacterId, name);
            AssertEqual(0, GetRewardGoodsDrawShowQuality(drawCardResponse.RewardGoodsList[0], $"{name} RewardGoods target character"), $"{name} RewardGoods target character ShowQuality stays zero so client uses character rank");
            DrawInfo clientDrawInfo = drawCardResponse.ClientDrawInfo
                ?? throw new InvalidDataException($"{name}: expected ClientDrawInfo after draw.");
            AssertEqual(preloadedDrawInfo.TotalCount + 1, clientDrawInfo.TotalCount, $"{name} ClientDrawInfo TotalCount after 1x draw");
            AssertEqual(eventConstruct1488.MaxBottomTimes, clientDrawInfo.BottomTimes, $"{name} ClientDrawInfo BottomTimes resets after forced target pity");
        }

        private static void AssertCharacterRewardGoodsHydrated(RewardGoods reward, int expectedCharacterId, string name)
        {
            int expectedQuality = TableReaderV2.Parse<CharacterQualityTable>()
                .Where(quality => quality.CharacterId == expectedCharacterId)
                .OrderBy(quality => quality.Quality)
                .FirstOrDefault()?.Quality
                ?? throw new InvalidDataException($"{name}: expected CharacterQualityTable row for character {expectedCharacterId}.");

            AssertEqual((int)RewardType.Character, reward.RewardType, $"{name} RewardGoods reward type");
            AssertEqual(expectedCharacterId, reward.TemplateId, $"{name} RewardGoods character id");
            AssertEqual(1, reward.Count, $"{name} RewardGoods count");
            AssertEqual(1, reward.Level, $"{name} RewardGoods level");
            AssertEqual(expectedQuality, reward.Quality, $"{name} RewardGoods table-derived Quality");
            AssertEqual(1, reward.Grade, $"{name} RewardGoods initial Grade");
        }

        private static void AssertDrawEquipRewardPushesRecycleFlag(DrawInfo drawInfo)
        {
            const long playerId = 880012;
            const int packetId = 8813;
            const string name = "Draw target equip reward NotifyEquipDataList recycle flag";
            if (!drawInfo.ResourceIds.TryGetValue(1, out int targetEquipId))
                throw new InvalidDataException($"{name}: expected ResourceIds[1] target equip id.");

            DrawInfo preloadedDrawInfo = PreloadDrawProgressToBottomTimes(playerId, drawInfo, desiredBottomTimes: 1, name);
            DrawDrawCardRequest drawCardRequest = new()
            {
                DrawId = drawInfo.Id,
                Count = 1,
                UseDrawTicketId = 0
            };
            DrawDrawCardRequest drawCardRequestRoundTrip = MessagePackSerializer.Deserialize<DrawDrawCardRequest>(
                MessagePackSerializer.Serialize(drawCardRequest));
            AssertEqual(drawInfo.Id, drawCardRequestRoundTrip.DrawId, $"{name} DrawDrawCardRequest DrawId MessagePack round-trip");
            AssertEqual(1, drawCardRequestRoundTrip.Count, $"{name} DrawDrawCardRequest Count MessagePack round-trip");
            AssertEqual(0, drawCardRequestRoundTrip.UseDrawTicketId, $"{name} DrawDrawCardRequest UseDrawTicketId MessagePack round-trip");

            DrawDrawCardResponse? drawCardResponse = null;
            NotifyEquipDataList? drawEquipPush = null;
            using (LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, [new Item { Id = drawInfo.UseItemId, Count = drawInfo.UseItemCount }]),
                "draw-target-equip-recycle-flag-compat-test"))
            {
                InvokeRegisteredRequestHandler("DrawDrawCardRequest", harness.Session, packetId, drawCardRequestRoundTrip);

                for (int packetIndex = 0; packetIndex < 8; packetIndex++)
                {
                    Packet packet = harness.ReadPacket($"{name} packet {packetIndex + 1}");
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        if (push.Name == nameof(NotifyEquipDataList))
                            drawEquipPush = MessagePackSerializer.Deserialize<NotifyEquipDataList>(push.Content);
                        continue;
                    }

                    AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} response packet type");
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(packetId, response.Id, $"{name} response packet id");
                    AssertEqual(nameof(DrawDrawCardResponse), response.Name, $"{name} response packet name");
                    drawCardResponse = MessagePackSerializer.Deserialize<DrawDrawCardResponse>(response.Content);
                    break;
                }
            }

            if (drawCardResponse is null)
                throw new InvalidDataException($"{name}: expected DrawDrawCardResponse after draw reward pushes.");
            if (drawEquipPush is null)
                throw new InvalidDataException($"{name}: expected NotifyEquipDataList for forced equip reward {targetEquipId}.");

            AssertEqual(1, drawCardResponse.RewardGoodsList.Count, $"{name} RewardGoodsList count");
            RewardGoods reward = drawCardResponse.RewardGoodsList[0];
            AssertEqual((int)RewardType.Equip, reward.RewardType, $"{name} RewardGoods reward type");
            AssertEqual(targetEquipId, reward.TemplateId, $"{name} RewardGoods target equip id");
            AssertEqual(1, drawEquipPush.EquipDataList.Count, $"{name} NotifyEquipDataList equip count");
            EquipData equipReward = drawEquipPush.EquipDataList.Single(equip => equip.TemplateId == (uint)targetEquipId);
            AssertEqual(true, equipReward.IsRecycle, $"{name} EquipData.IsRecycle");
            DrawInfo clientDrawInfo = drawCardResponse.ClientDrawInfo
                ?? throw new InvalidDataException($"{name}: expected ClientDrawInfo after draw.");
            AssertEqual(preloadedDrawInfo.TotalCount + 1, clientDrawInfo.TotalCount, $"{name} ClientDrawInfo TotalCount after 1x draw");
        }

        private static DrawInfo PreloadDrawProgressToBottomTimes(long playerId, DrawInfo drawInfo, int desiredBottomTimes, string name)
        {
            if (drawInfo.MaxBottomTimes <= 0)
                throw new InvalidDataException($"{name}: draw {drawInfo.Id} must have positive MaxBottomTimes.");
            if (desiredBottomTimes <= 0 || desiredBottomTimes >= drawInfo.MaxBottomTimes)
                throw new InvalidDataException($"{name}: desired BottomTimes {desiredBottomTimes}/{drawInfo.MaxBottomTimes} cannot cross pity in one 10x draw.");

            int progressToApply = drawInfo.BottomTimes - desiredBottomTimes;
            if (progressToApply < 0)
                progressToApply += drawInfo.MaxBottomTimes;

            DrawInfo preloadedDrawInfo = InvokeApplyDrawProgress(playerId, drawInfo.Id, progressToApply, name);
            AssertEqual(desiredBottomTimes, preloadedDrawInfo.BottomTimes, $"{name} preloaded BottomTimes");
            return preloadedDrawInfo;
        }

        private static DrawInfo InvokeApplyDrawProgress(long playerId, int drawId, int count, string name)
        {
            Type drawManagerType = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
            MethodInfo applyDrawProgress = RequiredMethod(
                drawManagerType,
                "ApplyDrawProgress",
                BindingFlags.Static | BindingFlags.Public,
                [typeof(long), typeof(int), typeof(int)]);
            return applyDrawProgress.Invoke(null, [playerId, drawId, count]) as DrawInfo
                ?? throw new InvalidDataException($"{name}: DrawManager.ApplyDrawProgress returned nil or an unexpected payload.");
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

        private static void AssertCurrentWeaponBannerShopFlags(IReadOnlyDictionary<int, bool> expectedIsShowShopByDrawId, IReadOnlyList<DrawInfo> drawInfos)
        {
            Dictionary<int, DrawInfo> drawInfoById = drawInfos.ToDictionary(info => info.Id);
            foreach ((int drawId, bool expectedIsShowShop) in expectedIsShowShopByDrawId.OrderBy(entry => entry.Key))
            {
                if (!drawInfoById.TryGetValue(drawId, out DrawInfo? drawInfo))
                    throw new InvalidDataException($"Current weapon-banner draw {drawId}: expected DrawInfo row for IsShowShop retail flag.");
                AssertEqual(expectedIsShowShop, drawInfo.IsShowShop, $"Current weapon-banner draw {drawId} IsShowShop retail flag");
            }
        }

        private static void AssertCurrentWeaponBannerDrawPreviewRows(IReadOnlyList<int> expectedDrawIds, IReadOnlyList<DrawInfo> drawInfos)
        {
            Dictionary<int, DrawInfo> drawInfoById = drawInfos.ToDictionary(info => info.Id);
            List<DrawPreviewTable> previewRows = TableReaderV2.Parse<DrawPreviewTable>();
            Dictionary<int, EquipTable> equipRowsById = TableReaderV2.Parse<EquipTable>().ToDictionary(equip => equip.Id);

            foreach (int drawId in expectedDrawIds)
            {
                if (!drawInfoById.TryGetValue(drawId, out DrawInfo? drawInfo))
                    throw new InvalidDataException($"Current weapon-banner DrawPreview row {drawId}: expected served DrawInfo row.");
                if (!drawInfo.ResourceIds.TryGetValue(1, out int targetEquipId))
                    throw new InvalidDataException($"Current weapon-banner DrawPreview row {drawId}: expected served DrawInfo ResourceIds[1] target equip id.");

                DrawPreviewTable preview = previewRows.SingleOrDefault(preview => preview.Id == drawId)
                    ?? throw new InvalidDataException($"Current weapon-banner DrawPreview row {drawId}: expected DrawPreview.tsv row for served current weapon banner.");
                AssertEqual(true, preview.UpGoodsId.Contains(targetEquipId), $"Current weapon-banner DrawPreview row {drawId} UpGoodsId contains target equip {targetEquipId}");

                int[] goodsEquipIds = preview.GoodsId.Distinct().ToArray();
                if (goodsEquipIds.Length == 0)
                    throw new InvalidDataException($"Current weapon-banner DrawPreview row {drawId}: expected non-empty GoodsId equip pool.");
                if (goodsEquipIds.Length < 2)
                    throw new InvalidDataException($"Current weapon-banner DrawPreview row {drawId}: expected GoodsId to contain an off-banner equip pair.");
                if (goodsEquipIds.Contains(targetEquipId))
                    throw new InvalidDataException($"Current weapon-banner DrawPreview row {drawId}: expected GoodsId off-banner pool not to repeat target equip {targetEquipId}.");
                foreach (int equipId in goodsEquipIds)
                {
                    if (!equipRowsById.TryGetValue(equipId, out EquipTable? equip))
                        throw new InvalidDataException($"Current weapon-banner DrawPreview row {drawId}: expected GoodsId {equipId} to resolve to an EquipTable row.");
                    if (equip.Type <= 0)
                        throw new InvalidDataException($"Current weapon-banner DrawPreview row {drawId}: expected GoodsId {equipId} to be a weapon equip, got Type {equip.Type}.");
                    AssertEqual(true, AscNet.Common.Database.Character.IsOwnableEquipTemplate(equip), $"Current weapon-banner DrawPreview row {drawId} GoodsId {equipId} ownable weapon template");
                }
            }
        }


        private static void AssertCurrentOwnableWeaponBreakthroughCoverage()
        {
            Dictionary<int, int> expectedBreakthroughCountsByQuality = new()
            {
                [2] = 1,
                [3] = 3,
                [4] = 4,
                [5] = 5,
                [6] = 5
            };
            EquipTable[] ownableWeaponRows = TableReaderV2.Parse<EquipTable>()
                .Where(equip => equip.Site == 0
                    && equip.Type != 0
                    && equip.Type != 99
                    && equip.Quality > 0
                    && equip.Priority != 100)
                .OrderBy(equip => equip.Id)
                .ToArray();
            if (ownableWeaponRows.Length == 0)
                throw new InvalidDataException("Current ownable weapon breakthrough coverage: expected at least one current-client ownable weapon EquipTable row.");

            ILookup<int, EquipBreakThroughTable> breakthroughRowsByEquipId = TableReaderV2.Parse<EquipBreakThroughTable>()
                .ToLookup(breakthrough => breakthrough.EquipId);
            foreach (EquipTable weaponRow in ownableWeaponRows)
            {
                if (!expectedBreakthroughCountsByQuality.TryGetValue(weaponRow.Quality, out int expectedBreakthroughCount))
                    throw new InvalidDataException($"Current ownable weapon {weaponRow.Id}: unsupported EquipTable quality {weaponRow.Quality} for breakthrough coverage.");

                EquipBreakThroughTable[] breakthroughRows = breakthroughRowsByEquipId[weaponRow.Id]
                    .OrderBy(breakthrough => breakthrough.Times)
                    .ToArray();
                AssertEqual(expectedBreakthroughCount, breakthroughRows.Length, $"Current ownable weapon {weaponRow.Id} breakthrough row count for quality {weaponRow.Quality}");

                for (int times = 0; times < expectedBreakthroughCount; times++)
                {
                    EquipBreakThroughTable breakthroughRow = breakthroughRows.SingleOrDefault(breakthrough => breakthrough.Times == times)
                        ?? throw new InvalidDataException($"Current ownable weapon {weaponRow.Id}: missing EquipBreakThrough row for breakthrough {times}.");
                    AssertEqual(true, breakthroughRow.LevelLimit > 0, $"Current ownable weapon {weaponRow.Id} breakthrough {times} LevelLimit nonzero");
                    AssertEqual(true, breakthroughRow.LevelUpTemplateId > 0, $"Current ownable weapon {weaponRow.Id} breakthrough {times} LevelUpTemplateId nonzero");
                }
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
            bool sawMismatchedNonzeroFashionRepair = false;
            bool preferVeraMismatchedFashionRepair = characterIds.Distinct().Contains(1131004);

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
                    if (characterId == 1131004 || (!preferVeraMismatchedFashionRepair && !sawMismatchedNonzeroFashionRepair))
                    {
                        sawMismatchedNonzeroFashionRepair = true;
                        AssertMismatchedNonzeroFashionRepair(
                            characterId,
                            characterRow,
                            firstQualityRow,
                            expectedSkillIds,
                            fashionRowsById,
                            equipRowsById);
                    }
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
            if (!sawMismatchedNonzeroFashionRepair)
                throw new InvalidDataException("Current draw target characters: expected at least one row to cover mismatched nonzero fashion/head-fashion repair.");
        }

        private static void AssertMismatchedNonzeroFashionRepair(
            int characterId,
            CharacterTable characterRow,
            CharacterQualityTable firstQualityRow,
            IReadOnlyList<uint> expectedSkillIds,
            IReadOnlyDictionary<int, FashionTable> fashionRowsById,
            IReadOnlyDictionary<int, EquipTable> equipRowsById)
        {
            int defaultFashionId = characterRow.DefaultNpcFashtionId;
            if (defaultFashionId <= 0)
                throw new InvalidDataException($"Current draw target character {characterId}: expected a positive default fashion id for mismatched fashion repair coverage.");
            if (!fashionRowsById.TryGetValue(defaultFashionId, out FashionTable? defaultFashionRow))
                throw new InvalidDataException($"Current draw target character {characterId}: expected FashionTable row {defaultFashionId} for mismatched fashion repair coverage.");
            AssertEqual(characterId, defaultFashionRow.CharacterId, $"Current draw target character {characterId} mismatched repair default fashion CharacterId");

            FashionTable mismatchedFashionRow = fashionRowsById.Values
                .Where(fashion => fashion.Id > 0 && fashion.CharacterId != characterId)
                .OrderBy(fashion => fashion.Id)
                .FirstOrDefault()
                ?? throw new InvalidDataException($"Current draw target character {characterId}: expected at least one valid Fashion.tsv row from another character for mismatched fashion repair coverage.");

            AscNet.Common.Database.Character persistedCharacter = new()
            {
                Uid = characterId,
                Characters =
                [
                    new CharacterData
                    {
                        Id = (uint)characterId,
                        Level = 1,
                        Quality = firstQualityRow.Quality,
                        InitQuality = firstQualityRow.Quality,
                        Grade = 1,
                        TrustLv = 1,
                        LiberateLv = 1,
                        CreateTime = 1,
                        SkillList = expectedSkillIds
                            .Select(skillId => new CharacterSkill { Id = skillId, Level = 1 })
                            .ToList(),
                        EnhanceSkillList = [],
                        FashionId = (uint)mismatchedFashionRow.Id,
                        CharacterHeadInfo = new()
                        {
                            HeadFashionId = (uint)mismatchedFashionRow.Id,
                            HeadFashionType = 1
                        }
                    }
                ],
                Equips = [],
                Fashions =
                [
                    new FashionList
                    {
                        Id = mismatchedFashionRow.Id,
                        IsLock = false
                    }
                ]
            };

            if (characterRow.EquipId > 0
                && equipRowsById.TryGetValue(characterRow.EquipId, out EquipTable? defaultEquipRow)
                && AscNet.Common.Database.Character.IsOwnableEquipTemplate(defaultEquipRow))
            {
                persistedCharacter.Equips.Add(new EquipData
                {
                    Id = 990002,
                    TemplateId = (uint)characterRow.EquipId,
                    CharacterId = characterId,
                    Level = 1,
                    ResonanceInfo = [],
                    UnconfirmedResonanceInfo = [],
                    AwakeSlotList = [],
                    WeaponOverrunData = new()
                });
            }

            AssertEqual(true, persistedCharacter.NormalizeCharactersForCurrentTables(), $"Current draw target character {characterId} mismatched nonzero fashion repair reports mutation");
            CharacterData repairedCharacter = persistedCharacter.Characters.Single(character => character.Id == characterId);
            AssertEqual((uint)defaultFashionId, repairedCharacter.FashionId, $"Current draw target character {characterId} mismatched nonzero FashionId resets to default");
            if (repairedCharacter.CharacterHeadInfo is null)
                throw new InvalidDataException($"Current draw target character {characterId}: expected mismatched fashion repair to preserve CharacterHeadInfo.");
            AssertEqual((uint)defaultFashionId, repairedCharacter.CharacterHeadInfo.HeadFashionId, $"Current draw target character {characterId} mismatched nonzero HeadFashionId resets to default");
            FashionList repairedDefaultFashion = persistedCharacter.Fashions.Single(fashion => fashion.Id == defaultFashionId);
            AssertEqual(false, repairedDefaultFashion.IsLock, $"Current draw target character {characterId} mismatched nonzero repair default fashion unlocked");
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

            int pityPullOffset = drawInfo.BottomTimes - 1;
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
            int pityPullOffset = drawInfo.BottomTimes - 1;
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
                    AssertEqual(true, equipReward.IsRecycle, $"{name} equip reward recycle flag");
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

        private static void ValidateMissingFeatureCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForMissingFeatureCompatibility();

            const long playerId = 99_001;
            const long headPortraitId = 9000003;
            const long headFrameId = 2203001;
            const long medalId = 50001;
            const long chatBoardId = 2100001;

            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PlayerData.Sign = "MissingFeatureCompatibility";
            player.PlayerData.CurrHeadPortraitId = 0;
            player.PlayerData.CurrHeadFrameId = 0;
            player.PlayerData.CurrMedalId = 0;
            player.PlayerData.CurrentChatBoardId = 0;
            player.PlayerData.LastLoginTime = 1_720_000_001;

            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(new CharacterData
            {
                Id = 1021001,
                Level = 80
            });

            using LoopbackSessionHarness harness = new(
                character,
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                "missing-feature-compat-test");

            const int setHeadPortraitPacketId = 11_001;
            InvokeRegisteredRequestHandler(
                nameof(SetHeadPortraitRequest),
                harness.Session,
                setHeadPortraitPacketId,
                new SetHeadPortraitRequest { Id = headPortraitId });
            SetHeadPortraitResponse setHeadPortraitResponse = ReadResponsePayload<SetHeadPortraitResponse>(
                harness,
                setHeadPortraitPacketId,
                nameof(SetHeadPortraitResponse),
                "SetHeadPortraitRequest response");
            AssertEqual(0, setHeadPortraitResponse.Code, "SetHeadPortraitResponse Code");
            AssertEqual(headPortraitId, player.PlayerData.CurrHeadPortraitId, "SetHeadPortraitRequest persisted CurrHeadPortraitId");

            const int setHeadFramePacketId = 11_002;
            InvokeRegisteredRequestHandler(
                nameof(SetHeadFrameRequest),
                harness.Session,
                setHeadFramePacketId,
                new SetHeadFrameRequest { Id = headFrameId });
            SetHeadFrameResponse setHeadFrameResponse = ReadResponsePayload<SetHeadFrameResponse>(
                harness,
                setHeadFramePacketId,
                nameof(SetHeadFrameResponse),
                "SetHeadFrameRequest response");
            AssertEqual(0, setHeadFrameResponse.Code, "SetHeadFrameResponse Code");
            AssertEqual(headFrameId, player.PlayerData.CurrHeadFrameId, "SetHeadFrameRequest persisted CurrHeadFrameId");

            const int setCurrentMedalPacketId = 11_003;
            InvokeRegisteredRequestHandler(
                nameof(SetCurrentMedalRequest),
                harness.Session,
                setCurrentMedalPacketId,
                new SetCurrentMedalRequest { Id = medalId });
            NotifyPlayerCurrMedalId medalPush = ReadPushPayload<NotifyPlayerCurrMedalId>(
                harness,
                nameof(NotifyPlayerCurrMedalId),
                "SetCurrentMedalRequest medal push");
            AssertEqual(medalId, medalPush.CurrMedalId, "NotifyPlayerCurrMedalId CurrMedalId");
            SetCurrentMedalResponse setCurrentMedalResponse = ReadResponsePayload<SetCurrentMedalResponse>(
                harness,
                setCurrentMedalPacketId,
                nameof(SetCurrentMedalResponse),
                "SetCurrentMedalRequest response");
            AssertEqual(0, setCurrentMedalResponse.Code, "SetCurrentMedalResponse Code");
            AssertEqual(medalId, player.PlayerData.CurrMedalId, "SetCurrentMedalRequest persisted CurrMedalId");

            const int setCurChatBoardPacketId = 11_004;
            InvokeRegisteredRequestHandler(
                nameof(SetCurChatBoardRequest),
                harness.Session,
                setCurChatBoardPacketId,
                new SetCurChatBoardRequest { ChatBoardId = chatBoardId });
            NotifyCurChatBoardId chatBoardPush = ReadPushPayload<NotifyCurChatBoardId>(
                harness,
                nameof(NotifyCurChatBoardId),
                "SetCurChatBoardRequest chat-board push");
            AssertEqual(chatBoardId, chatBoardPush.CurrentChatBoardId, "NotifyCurChatBoardId CurrentChatBoardId");
            SetCurChatBoardResponse setCurChatBoardResponse = ReadResponsePayload<SetCurChatBoardResponse>(
                harness,
                setCurChatBoardPacketId,
                nameof(SetCurChatBoardResponse),
                "SetCurChatBoardRequest response");
            AssertEqual(0, setCurChatBoardResponse.Code, "SetCurChatBoardResponse Code");
            AssertEqual(chatBoardId, player.PlayerData.CurrentChatBoardId, "SetCurChatBoardRequest persisted CurrentChatBoardId");

            const int getPlayerInfoPacketId = 11_005;
            const uint unknownPlayerIdA = 990_000_001;
            const uint unknownPlayerIdB = 990_000_002;
            uint currentPlayerId = (uint)playerId;
            InvokeRegisteredRequestHandler(
                nameof(GetPlayerInfoListRequest),
                harness.Session,
                getPlayerInfoPacketId,
                new GetPlayerInfoListRequest { Ids = [currentPlayerId, unknownPlayerIdA, currentPlayerId, unknownPlayerIdB, unknownPlayerIdA] });
            GetPlayerInfoListResponse playerInfoListResponse = ReadResponsePayload<GetPlayerInfoListResponse>(
                harness,
                getPlayerInfoPacketId,
                nameof(GetPlayerInfoListResponse),
                "GetPlayerInfoListRequest response");
            AssertEqual(0, playerInfoListResponse.Code, "GetPlayerInfoListResponse Code");
            AssertEqual(3, playerInfoListResponse.PlayerInfoList.Count, "GetPlayerInfoListResponse distinct requested player count");
            AssertIntegerList(
                [currentPlayerId, unknownPlayerIdA, unknownPlayerIdB],
                playerInfoListResponse.PlayerInfoList.Select(info => (long)info.Id).ToArray(),
                "GetPlayerInfoListResponse distinct player ids in request order");
            GetPlayerInfoListResponse.GetPlayerInfoListResponsePlayerInfo playerInfo = playerInfoListResponse.PlayerInfoList[0];
            AssertEqual(currentPlayerId, playerInfo.Id, "GetPlayerInfoListResponse current PlayerInfo Id");
            AssertEqual(player.PlayerData.Name, playerInfo.Name, "GetPlayerInfoListResponse current PlayerInfo Name");
            AssertEqual((int)player.PlayerData.Level, playerInfo.Level, "GetPlayerInfoListResponse current PlayerInfo Level");
            AssertEqual(player.PlayerData.Sign, playerInfo.Sign, "GetPlayerInfoListResponse current PlayerInfo Sign");
            AssertEqual((uint)headPortraitId, playerInfo.CurrHeadPortraitId, "GetPlayerInfoListResponse current PlayerInfo CurrHeadPortraitId");
            AssertEqual((int)headFrameId, playerInfo.CurrHeadFrameId, "GetPlayerInfoListResponse current PlayerInfo CurrHeadFrameId");
            AssertEqual((uint)player.PlayerData.LastLoginTime, playerInfo.LastLoginTime, "GetPlayerInfoListResponse current PlayerInfo LastLoginTime");
            AssertEqual((int)medalId, playerInfo.CurrMedalId, "GetPlayerInfoListResponse current PlayerInfo CurrMedalId");
            AssertEqual(false, playerInfo.IsCancel, "GetPlayerInfoListResponse current PlayerInfo IsCancel");
            AssertEqual(0, playerInfo.DlcMultiplayerTitle, "GetPlayerInfoListResponse current PlayerInfo DlcMultiplayerTitle");
            for (int infoIndex = 1; infoIndex < playerInfoListResponse.PlayerInfoList.Count; infoIndex++)
            {
                GetPlayerInfoListResponse.GetPlayerInfoListResponsePlayerInfo unknownPlayerInfo = playerInfoListResponse.PlayerInfoList[infoIndex];
                if (string.IsNullOrWhiteSpace(unknownPlayerInfo.Name))
                    throw new InvalidDataException($"GetPlayerInfoListResponse unknown PlayerInfo {unknownPlayerInfo.Id} Name: expected non-empty fallback display name.");
            }

            const int votePacketId = 11_006;
            InvokeRegisteredRequestHandler(nameof(GetVoteGroupListRequest), harness.Session, votePacketId, new GetVoteGroupListRequest());
            GetVoteGroupListResponse voteGroupListResponse = ReadResponsePayload<GetVoteGroupListResponse>(
                harness,
                votePacketId,
                nameof(GetVoteGroupListResponse),
                "GetVoteGroupListRequest response");
            AssertEqual(true, voteGroupListResponse.VoteGroupList.Count > 0, "GetVoteGroupListResponse VoteGroupList non-empty");
            GetVoteGroupListResponse.GetVoteGroupListResponseVoteGroup voteGroup = voteGroupListResponse.VoteGroupList[0];
            AssertEqual(true, voteGroup.VoteDic is not null && voteGroup.VoteDic.Count > 0, "GetVoteGroupListResponse VoteGroupList[0] VoteDic non-empty");

            const int guildDetailPacketId = 11_007;
            InvokeRegisteredRequestHandler(
                nameof(GuildListDetailRequest),
                harness.Session,
                guildDetailPacketId,
                new GuildListDetailRequest { GuildId = 365 });
            GuildListDetailResponse guildDetailResponse = ReadResponsePayload<GuildListDetailResponse>(
                harness,
                guildDetailPacketId,
                nameof(GuildListDetailResponse),
                "GuildListDetailRequest response");
            AssertEqual(0, guildDetailResponse.Code, "GuildListDetailResponse Code");
            AssertEqual(365U, guildDetailResponse.GuildId, "GuildListDetailResponse GuildId");
            AssertEqual(player.PlayerData.Name, guildDetailResponse.GuildLeaderName, "GuildListDetailResponse GuildLeaderName");
            if (guildDetailResponse.GiftLevelGot is null)
                throw new InvalidDataException("GuildListDetailResponse GiftLevelGot: expected initialized list.");

            const int guildMemberPacketId = 11_008;
            InvokeRegisteredRequestHandler(
                nameof(GuildMemberDetailRequest),
                harness.Session,
                guildMemberPacketId,
                new GuildMemberDetailRequest { GuildId = 365 });
            GuildMemberDetailResponse guildMemberResponse = ReadResponsePayload<GuildMemberDetailResponse>(
                harness,
                guildMemberPacketId,
                nameof(GuildMemberDetailResponse),
                "GuildMemberDetailRequest response");
            AssertEqual(0, guildMemberResponse.Code, "GuildMemberDetailResponse Code");
            AssertEqual(1, guildMemberResponse.MembersData.Count, "GuildMemberDetailResponse MembersData count");
            GuildMemberDetailResponse.GuildMemberDetailResponseMembersData guildMember = guildMemberResponse.MembersData.Single();
            AssertEqual((uint)playerId, guildMember.Id, "GuildMemberDetailResponse current member Id");
            AssertEqual(player.PlayerData.Name, guildMember.Name, "GuildMemberDetailResponse current member Name");

            const int guildChatPacketId = 11_009;
            InvokeRegisteredRequestHandler(nameof(GuildListChatRequest), harness.Session, guildChatPacketId, new GuildListChatRequest());
            GuildListChatResponse guildChatResponse = ReadResponsePayload<GuildListChatResponse>(
                harness,
                guildChatPacketId,
                nameof(GuildListChatResponse),
                "GuildListChatRequest response");
            AssertEqual(0, guildChatResponse.Code, "GuildListChatResponse Code");
            if (guildChatResponse.ChatList is null)
                throw new InvalidDataException("GuildListChatResponse ChatList: expected initialized list.");
            AssertEqual(1, guildChatResponse.ChatList.Count, "GuildListChatResponse ChatList count");
            JObject guildChat = JObject.Parse(guildChatResponse.ChatList.Single());
            AssertEqual(6, guildChat.Value<int>("ChannelType"), "GuildListChatResponse ChatList[0] ChannelType");
            AssertEqual(1, guildChat.Value<int>("MsgType"), "GuildListChatResponse ChatList[0] MsgType");
            AssertEqual("AscNet", guildChat.Value<string>("GuildName"), "GuildListChatResponse ChatList[0] GuildName");
            if (string.IsNullOrWhiteSpace(guildChat.Value<string>("Content")))
                throw new InvalidDataException("GuildListChatResponse ChatList[0] Content: expected visible text.");

            const int guildSupportPacketId = 11_010;
            InvokeRegisteredRequestHandler(
                nameof(GuildWarOpenSupportPanelRequest),
                harness.Session,
                guildSupportPacketId,
                new GuildWarOpenSupportPanelRequest());
            GuildWarOpenSupportPanelResponse guildSupportResponse = ReadResponsePayload<GuildWarOpenSupportPanelResponse>(
                harness,
                guildSupportPacketId,
                nameof(GuildWarOpenSupportPanelResponse),
                "GuildWarOpenSupportPanelRequest response");
            AssertEqual(0, guildSupportResponse.Code, "GuildWarOpenSupportPanelResponse Code");
            if (guildSupportResponse.SupportDetail is null)
                throw new InvalidDataException("GuildWarOpenSupportPanelResponse SupportDetail: expected initialized detail.");
            AssertEqual(1021001, guildSupportResponse.SupportDetail.CharacterId, "GuildWarOpenSupportPanelResponse SupportDetail CharacterId");
            if (guildSupportResponse.SupportDetail.ToAssistRecords is null
                || guildSupportResponse.SupportDetail.MyLogs is null
                || guildSupportResponse.SupportDetail.GetAssistRecords is null
                || guildSupportResponse.SupportDetail.MyAssistRecords is null)
                throw new InvalidDataException("GuildWarOpenSupportPanelResponse SupportDetail: expected initialized record lists.");

            const int fixedShopPacketId = 11_011;
            InvokeRegisteredRequestHandler(
                nameof(GetFixedShopListRequest),
                harness.Session,
                fixedShopPacketId,
                new GetFixedShopListRequest { IdList = [1436] });
            GetFixedShopListResponse fixedShopResponse = ReadResponsePayload<GetFixedShopListResponse>(
                harness,
                fixedShopPacketId,
                nameof(GetFixedShopListResponse),
                "GetFixedShopListRequest response");
            AssertEqual(0, fixedShopResponse.Code, "GetFixedShopListResponse Code");
            AssertEqual(1, fixedShopResponse.ClientShopList.Count, "GetFixedShopListResponse ClientShopList count");
            GetShopInfoResponse.GetShopInfoResponseClientShop fixedShop = fixedShopResponse.ClientShopList.Single();
            AssertEqual(1436U, fixedShop.Id, "GetFixedShopListResponse ClientShop Id");
            AssertEqual(true, fixedShop.ShowIds.Contains(50135), "GetFixedShopListResponse ClientShop ShowIds includes 50135");
            AssertEqual(3, fixedShop.GoodsList.Count, "GetFixedShopListResponse ClientShop GoodsList count");
            foreach (GetShopInfoResponse.GetShopInfoResponseClientShop.GetShopInfoResponseClientShopGoods fixedShopGoods in fixedShop.GoodsList)
            {
                if (fixedShopGoods.RewardGoods is null)
                    throw new InvalidDataException($"GetFixedShopListResponse goods {fixedShopGoods.Id} RewardGoods: expected initialized reward.");
                AssertEqual(false, fixedShopGoods.RewardGoods.IsGift, $"GetFixedShopListResponse goods {fixedShopGoods.Id} RewardGoods IsGift");
                AssertEqual(0, fixedShopGoods.RewardGoods.RewardMulti, $"GetFixedShopListResponse goods {fixedShopGoods.Id} RewardGoods RewardMulti");
                AssertEqual(0, fixedShopGoods.BuyPriority, $"GetFixedShopListResponse goods {fixedShopGoods.Id} BuyPriority");
                AssertEqual(0, fixedShopGoods.ActivityConsumeCount, $"GetFixedShopListResponse goods {fixedShopGoods.Id} ActivityConsumeCount");
                AssertEqual(0, fixedShopGoods.ActivityDiscount, $"GetFixedShopListResponse goods {fixedShopGoods.Id} ActivityDiscount");
            }

            const int lottoInfoPacketId = 11_012;
            InvokeRegisteredRequestHandler(nameof(LottoInfoRequest), harness.Session, lottoInfoPacketId, new LottoInfoRequest());
            LottoInfoResponse lottoInfoResponse = ReadResponsePayload<LottoInfoResponse>(
                harness,
                lottoInfoPacketId,
                nameof(LottoInfoResponse),
                "LottoInfoRequest response");
            AssertEqual(0, lottoInfoResponse.Code, "LottoInfoResponse Code");
            if (lottoInfoResponse.LottoInfos is null)
                throw new InvalidDataException("LottoInfoResponse LottoInfos: expected initialized list.");

            const int enterWorldChatPacketId = 11_013;
            InvokeRegisteredRequestHandler(nameof(EnterWorldChatRequest), harness.Session, enterWorldChatPacketId, new EnterWorldChatRequest());
            EnterWorldChatResponse enterWorldChatResponse = ReadResponsePayload<EnterWorldChatResponse>(
                harness,
                enterWorldChatPacketId,
                nameof(EnterWorldChatResponse),
                "EnterWorldChatRequest response");
            AssertEqual(0, enterWorldChatResponse.Code, "EnterWorldChatResponse Code");
            AssertEqual(1, enterWorldChatResponse.ChannelId, "EnterWorldChatResponse retail ChannelId");

            const int getWorldChannelInfoPacketId = 11_014;
            InvokeRegisteredRequestHandler(nameof(GetWorldChannelInfoRequest), harness.Session, getWorldChannelInfoPacketId, new GetWorldChannelInfoRequest());
            GetWorldChannelInfoResponse worldChannelInfoResponse = ReadResponsePayload<GetWorldChannelInfoResponse>(
                harness,
                getWorldChannelInfoPacketId,
                nameof(GetWorldChannelInfoResponse),
                "GetWorldChannelInfoRequest response");
            AssertEqual(0, worldChannelInfoResponse.Code, "GetWorldChannelInfoResponse Code");
            AssertEqual(8, worldChannelInfoResponse.ChannelInfos.Count, "GetWorldChannelInfoResponse ChannelInfos count");
            for (int channelIndex = 0; channelIndex < worldChannelInfoResponse.ChannelInfos.Count; channelIndex++)
            {
                GetWorldChannelInfoResponse.GetWorldChannelInfoResponseChannelInfo channelInfo = worldChannelInfoResponse.ChannelInfos[channelIndex];
                AssertEqual(channelIndex, channelInfo.ChannelId, $"GetWorldChannelInfoResponse ChannelInfos[{channelIndex}] ChannelId");
                AssertEqual(0, channelInfo.PlayerNum, $"GetWorldChannelInfoResponse ChannelInfos[{channelIndex}] PlayerNum");
            }
        }

        private static void ValidateItemUseCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();

            const long playerId = 99_201;
            const int cogPackSmallId = 90_011;
            const int useCount = 2;
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(
                playerId,
                [
                    new Item { Id = cogPackSmallId, Count = 3 },
                    new Item { Id = AscNet.Common.Database.Inventory.Coin, Count = 100 }
                ]);
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                inventory,
                "item-use-compat-test");

            const int packetId = 14_001;
            AscNet.GameServer.Handlers.ItemUseRequest request = new()
            {
                Id = cogPackSmallId,
                Count = useCount
            };
            InvokeRegisteredRequestHandler(nameof(AscNet.GameServer.Handlers.ItemUseRequest), harness.Session, packetId, request);

            NotifyItemDataList consumePush = ReadPushPayload<NotifyItemDataList>(
                harness,
                nameof(NotifyItemDataList),
                "ItemUseRequest consumed pack push");
            Item consumedPack = consumePush.ItemDataList.Single(item => item.Id == cogPackSmallId);
            AssertEqual(1L, consumedPack.Count, "ItemUseRequest consumed Cog Pack count");

            NotifyItemDataList rewardPush = ReadPushPayload<NotifyItemDataList>(
                harness,
                nameof(NotifyItemDataList),
                "ItemUseRequest reward push");
            Item rewardedCogs = rewardPush.ItemDataList.Single(item => item.Id == AscNet.Common.Database.Inventory.Coin);
            AssertEqual(20_100L, rewardedCogs.Count, "ItemUseRequest rewarded Cog count");

            AscNet.GameServer.Handlers.ItemUseResponse response = ReadResponsePayload<AscNet.GameServer.Handlers.ItemUseResponse>(
                harness,
                packetId,
                nameof(AscNet.GameServer.Handlers.ItemUseResponse),
                "ItemUseRequest response");
            AssertEqual(0, response.Code, "ItemUseResponse Code");
            RewardGoods rewardGoods = response.RewardGoodsList.Single();
            AssertEqual(1, rewardGoods.RewardType, "ItemUseResponse RewardGoodsList[0] RewardType");
            AssertEqual(AscNet.Common.Database.Inventory.Coin, rewardGoods.TemplateId, "ItemUseResponse RewardGoodsList[0] TemplateId");
            AssertEqual(20_000, rewardGoods.Count, "ItemUseResponse RewardGoodsList[0] Count");
        }

        private static void ValidateOverclockMaterialBoxCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            AssertOverclockMaterialBoxUse(
                itemId: 60_001,
                expectedRewardPool: [40_100, 40_101, 40_102, 40_103, 40_104],
                packetId: 14_101,
                playerId: 99_301,
                name: "ItemUseRequest low-grade Overclock Material box");
            AssertOverclockMaterialBoxUse(
                itemId: 60_002,
                expectedRewardPool: [40_110, 40_111, 40_112, 40_113, 40_114],
                packetId: 14_102,
                playerId: 99_302,
                name: "ItemUseRequest high-grade Overclock Material box");

            AssertOverclockMaterialBoxUse(
                itemId: 90_101,
                expectedRewardPool: [40_100, 40_101, 40_102, 40_103, 40_104],
                packetId: 14_103,
                playerId: 99_303,
                name: "ItemUseRequest activity low-grade Overclock Material box");

            AssertOverclockMaterialBoxUse(
                itemId: 90_110,
                expectedRewardPool: [40_110, 40_111, 40_112, 40_113, 40_114],
                packetId: 14_104,
                playerId: 99_304,
                name: "ItemUseRequest activity high-grade Overclock Material box");

        }

        private static void AssertOverclockMaterialBoxUse(int itemId, int[] expectedRewardPool, int packetId, long playerId, string name)
        {
            const int useCount = 1;
            int[] unopenedBoxItemIds = [60_001, 60_002, 90_101, 90_110];
            Dictionary<int, ItemTable> itemRowsById = TableReaderV2.Parse<ItemTable>().ToDictionary(item => item.Id);
            ItemTable boxItem = itemRowsById.TryGetValue(itemId, out ItemTable? itemRow)
                ? itemRow
                : throw new InvalidDataException($"{name}: expected Item.tsv row {itemId}.");
            if (boxItem.SubTypeParams.Count == 0)
                throw new InvalidDataException($"{name} Item.tsv row {boxItem.Id} {boxItem.Name}: expected SubTypeParams[0] to mark random gift box behavior.");
            AssertEqual(2, boxItem.SubTypeParams[0], $"{name} Item.tsv row {boxItem.Id} {boxItem.Name} SubTypeParams[0] random gift box behavior");
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(
                playerId,
                [new Item { Id = itemId, Count = 1 }]);
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                inventory,
                $"{name}-compat-test");

            AscNet.GameServer.Handlers.ItemUseRequest request = new()
            {
                Id = itemId,
                Count = useCount
            };
            InvokeRegisteredRequestHandler(nameof(AscNet.GameServer.Handlers.ItemUseRequest), harness.Session, packetId, request);

            NotifyItemDataList consumePush = ReadPushPayload<NotifyItemDataList>(
                harness,
                nameof(NotifyItemDataList),
                $"{name} consumed box push");
            Item consumedBox = consumePush.ItemDataList.Single(item => item.Id == itemId);
            AssertEqual(0L, consumedBox.Count, $"{name} consumed box count");

            NotifyItemDataList rewardPush = ReadPushPayload<NotifyItemDataList>(
                harness,
                nameof(NotifyItemDataList),
                $"{name} reward push");
            AssertEqual(1, rewardPush.ItemDataList.Count(item => expectedRewardPool.Contains(item.Id)), $"{name} reward push contains exactly one expected-pool material");
            AssertEqual(false, rewardPush.ItemDataList.Any(item => unopenedBoxItemIds.Contains(item.Id)), $"{name} reward push does not award unopened Overclock Material box ids");
            Item awardedMaterial = rewardPush.ItemDataList.Single(item => expectedRewardPool.Contains(item.Id));
            AssertEqual(1L, awardedMaterial.Count, $"{name} NotifyItemDataList awarded material count");

            AscNet.GameServer.Handlers.ItemUseResponse response = ReadResponsePayload<AscNet.GameServer.Handlers.ItemUseResponse>(
                harness,
                packetId,
                nameof(AscNet.GameServer.Handlers.ItemUseResponse),
                $"{name} response");
            AssertEqual(0, response.Code, $"{name} ItemUseResponse Code");
            RewardGoods rewardGoods = response.RewardGoodsList.Single();
            AssertEqual((int)RewardType.Item, rewardGoods.RewardType, $"{name} RewardGoodsList[0] RewardType");
            AssertEqual(true, expectedRewardPool.Contains(rewardGoods.TemplateId), $"{name} RewardGoodsList[0] TemplateId expected pool membership");
            AssertEqual(false, unopenedBoxItemIds.Contains(rewardGoods.TemplateId), $"{name} RewardGoodsList[0] TemplateId is not an unopened Overclock Material box");
            AssertEqual(1, rewardGoods.Count, $"{name} RewardGoodsList[0] Count");
            AssertEqual(awardedMaterial.Id, rewardGoods.TemplateId, $"{name} response/push awarded material TemplateId match");
            AssertEqual((int)awardedMaterial.Count, rewardGoods.Count, $"{name} response/push awarded material Count match");
        }

        private static void ValidateChatCompatibility()
        {
            const long playerId = 99_301;
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "chat-compat-test");

            const int enterWorldChatPacketId = 15_001;
            InvokeRegisteredRequestHandler(
                nameof(AscNet.GameServer.Handlers.EnterWorldChatRequest),
                harness.Session,
                enterWorldChatPacketId,
                new AscNet.GameServer.Handlers.EnterWorldChatRequest());
            EnterWorldChatResponse enterWorldChatResponse = ReadResponsePayload<EnterWorldChatResponse>(
                harness,
                enterWorldChatPacketId,
                nameof(EnterWorldChatResponse),
                "EnterWorldChatRequest response");
            AssertEqual(0, enterWorldChatResponse.Code, "EnterWorldChatResponse Code");
            AssertEqual(1, enterWorldChatResponse.ChannelId, "EnterWorldChatResponse retail ChannelId");
            if (harness.TryReadAvailablePacket("EnterWorldChatRequest unexpected post-response packet", out Packet enterWorldChatUnexpectedPacket))
                throw new InvalidDataException($"EnterWorldChatRequest: expected only EnterWorldChatResponse before GetWorldChannelInfoRequest, got {enterWorldChatUnexpectedPacket.Type}.");

            const int getWorldChannelInfoPacketId = 15_002;
            InvokeRegisteredRequestHandler(
                nameof(AscNet.GameServer.Handlers.GetWorldChannelInfoRequest),
                harness.Session,
                getWorldChannelInfoPacketId,
                new AscNet.GameServer.Handlers.GetWorldChannelInfoRequest());
            GetWorldChannelInfoResponse worldChannelInfoResponse = ReadResponsePayload<GetWorldChannelInfoResponse>(
                harness,
                getWorldChannelInfoPacketId,
                nameof(GetWorldChannelInfoResponse),
                "GetWorldChannelInfoRequest response");
            AssertEqual(0, worldChannelInfoResponse.Code, "GetWorldChannelInfoResponse Code");
            AssertEqual(8, worldChannelInfoResponse.ChannelInfos.Count, "GetWorldChannelInfoResponse ChannelInfos count");
            for (int channelIndex = 0; channelIndex < worldChannelInfoResponse.ChannelInfos.Count; channelIndex++)
            {
                GetWorldChannelInfoResponse.GetWorldChannelInfoResponseChannelInfo channelInfo = worldChannelInfoResponse.ChannelInfos[channelIndex];
                AssertEqual(channelIndex, channelInfo.ChannelId, $"GetWorldChannelInfoResponse ChannelInfos[{channelIndex}] ChannelId");
                AssertEqual(0, channelInfo.PlayerNum, $"GetWorldChannelInfoResponse ChannelInfos[{channelIndex}] PlayerNum");
            }

            const int sendChatPacketId = 15_003;
            InvokeRegisteredRequestHandler(
                nameof(AscNet.GameServer.Handlers.SendChatRequest),
                harness.Session,
                sendChatPacketId,
                new AscNet.GameServer.Handlers.SendChatRequest
                {
                    TargetIdList = [playerId],
                    ChatData = new AscNet.GameServer.Handlers.ChatData
                    {
                        ChannelType = AscNet.GameServer.Handlers.ChatChannelType.World,
                        MsgType = AscNet.GameServer.Handlers.ChatMsgType.Normal,
                        Content = "\ntest"
                    }
                });
            NotifyChatMessage notifyChatMessage = ReadPushPayload<NotifyChatMessage>(
                harness,
                nameof(NotifyChatMessage),
                "SendChatRequest NotifyChatMessage push");
            AssertEqual(playerId, notifyChatMessage.SenderId, "NotifyChatMessage SenderId");
            AssertEqual(AscNet.GameServer.Handlers.ChatChannelType.World, notifyChatMessage.ChannelType, "NotifyChatMessage ChannelType");
            AssertEqual("test", notifyChatMessage.Content, "NotifyChatMessage Content");
            SendChatResponse sendChatResponse = ReadResponsePayload<SendChatResponse>(
                harness,
                sendChatPacketId,
                nameof(SendChatResponse),
                "SendChatRequest response");
            AssertEqual(0, sendChatResponse.Code, "SendChatResponse Code");
            AssertEqual(0L, sendChatResponse.RefreshTime, "SendChatResponse RefreshTime");
            AscNet.GameServer.Handlers.NotifyWorldChat worldChatPush = ReadPushPayload<AscNet.GameServer.Handlers.NotifyWorldChat>(
                harness,
                nameof(AscNet.GameServer.Handlers.NotifyWorldChat),
                "SendChatRequest empty world-chat push");
            AssertEqual(0, worldChatPush.ChatMessages.Count, "NotifyWorldChat ChatMessages count");

            ValidateDeferredPreLoginChatCompatibility();

            static void ValidateDeferredPreLoginChatCompatibility()
            {
                const long playerId = 99_302;
                AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
                using LoopbackSessionHarness harness = new(
                    CreateDrawCompatibilityCharacter(playerId),
                    player,
                    CreateDrawCompatibilityInventory(playerId, []),
                    "chat-deferred-compat-test");
                harness.Session.player = null!;

                const int enterWorldChatPacketId = 15_101;
                InvokeRegisteredRequestHandler(
                    nameof(AscNet.GameServer.Handlers.EnterWorldChatRequest),
                    harness.Session,
                    enterWorldChatPacketId,
                    new AscNet.GameServer.Handlers.EnterWorldChatRequest());
                const int getWorldChannelInfoPacketId = 15_102;
                InvokeRegisteredRequestHandler(
                    nameof(AscNet.GameServer.Handlers.GetWorldChannelInfoRequest),
                    harness.Session,
                    getWorldChannelInfoPacketId,
                    new AscNet.GameServer.Handlers.GetWorldChannelInfoRequest());
                if (harness.TryReadAvailablePacket("deferred pre-login chat unexpected packet", out Packet unexpectedPacket))
                    throw new InvalidDataException($"Deferred pre-login chat: expected no packets before login flush, got {unexpectedPacket.Type}.");

                harness.Session.player = player;
                Type chatModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ChatModule");
                MethodInfo flushPendingLoginChat = RequiredMethod(chatModule, "FlushPendingLoginChat", BindingFlags.Static | BindingFlags.Public, [typeof(Session)]);
                flushPendingLoginChat.Invoke(null, [harness.Session]);

                EnterWorldChatResponse enterWorldChatResponse = ReadResponsePayload<EnterWorldChatResponse>(
                    harness,
                    enterWorldChatPacketId,
                    nameof(EnterWorldChatResponse),
                    "deferred EnterWorldChatRequest response");
                AssertEqual(0, enterWorldChatResponse.Code, "deferred EnterWorldChatResponse Code");
                AssertEqual(1, enterWorldChatResponse.ChannelId, "deferred EnterWorldChatResponse retail ChannelId");
                GetWorldChannelInfoResponse worldChannelInfoResponse = ReadResponsePayload<GetWorldChannelInfoResponse>(
                    harness,
                    getWorldChannelInfoPacketId,
                    nameof(GetWorldChannelInfoResponse),
                    "deferred GetWorldChannelInfoRequest response");
                AssertEqual(8, worldChannelInfoResponse.ChannelInfos.Count, "deferred GetWorldChannelInfoResponse ChannelInfos count");
                if (harness.TryReadAvailablePacket("deferred pre-login chat unexpected trailing packet", out Packet unexpectedTrailingPacket))
                    throw new InvalidDataException($"Deferred pre-login chat: expected no trailing packets after flush, got {unexpectedTrailingPacket.Type}.");
            }
        }

        private static void ValidateShopCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();

            const long playerId = 99_101;
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(
                playerId,
                [new Item { Id = 1, Count = 30_000 }]);
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                inventory,
                "shop-compat-test");

            const int shopInfoPacketId = 12_001;
            InvokeRegisteredRequestHandler(
                nameof(GetShopInfoRequest),
                harness.Session,
                shopInfoPacketId,
                new GetShopInfoRequest { Id = 1 });
            GetShopInfoResponse shopInfoResponse = ReadResponsePayload<GetShopInfoResponse>(
                harness,
                shopInfoPacketId,
                nameof(GetShopInfoResponse),
                "GetShopInfoRequest shop 1 response");
            AssertEqual(0, shopInfoResponse.Code, "GetShopInfoResponse Code");
            AssertSupplyShopClientShop(shopInfoResponse.ClientShop, "GetShopInfoResponse ClientShop");

            const int fixedShopPacketId = 12_002;
            InvokeRegisteredRequestHandler(
                nameof(GetFixedShopListRequest),
                harness.Session,
                fixedShopPacketId,
                new GetFixedShopListRequest { IdList = [502, 504] });
            GetFixedShopListResponse fixedShopResponse = ReadResponsePayload<GetFixedShopListResponse>(
                harness,
                fixedShopPacketId,
                nameof(GetFixedShopListResponse),
                "GetFixedShopListRequest 502/504 response");
            AssertEqual(0, fixedShopResponse.Code, "GetFixedShopListResponse 502/504 Code");
            AssertEqual(2, fixedShopResponse.ClientShopList.Count, "GetFixedShopListResponse 502/504 ClientShopList count");
            AssertIntegerList(
                [502, 504],
                fixedShopResponse.ClientShopList.Select(shop => (long)shop.Id).Order().ToArray(),
                "GetFixedShopListResponse 502/504 ClientShop ids");
            foreach (uint shopId in new uint[] { 502, 504 })
            {
                GetShopInfoResponse.GetShopInfoResponseClientShop clientShop = fixedShopResponse.ClientShopList.Single(shop => shop.Id == shopId);
                AssertEqual(true, clientShop.GoodsList.Count > 0, $"GetFixedShopListResponse shop {shopId} GoodsList non-empty");
            }

            Type getShopValidInfoRequestType = RequiredShopMessageType("GetShopValidInfoRequest");
            Type getShopValidInfoResponseType = RequiredShopMessageType("GetShopValidInfoResponse");
            object getShopValidInfoRequest = Activator.CreateInstance(getShopValidInfoRequestType)
                ?? throw new InvalidDataException("GetShopValidInfoRequest: expected a public parameterless constructor for MessagePack.");
            SetFirstAvailableIntegerList(
                getShopValidInfoRequest,
                ["ShopIds", "ShopIdList", "IdList"],
                [502, 504],
                "GetShopValidInfoRequest shop ids");

            const int validInfoPacketId = 12_003;
            InvokeRegisteredRequestHandler(
                "GetShopValidInfoRequest",
                harness.Session,
                validInfoPacketId,
                getShopValidInfoRequest);
            object getShopValidInfoResponse = ReadResponsePayload(
                harness,
                validInfoPacketId,
                "GetShopValidInfoResponse",
                "GetShopValidInfoRequest 502/504 response",
                getShopValidInfoResponseType);
            AssertEqual(0, GetRequiredIntegerMember(getShopValidInfoResponse, "Code"), "GetShopValidInfoResponse Code");
            List<object> shopValidInfos = GetRequiredObjectListMember(
                getShopValidInfoResponse,
                ["ShopValidInfos", "ShopValidInfoList", "ValidInfoList"],
                "GetShopValidInfoResponse ShopValidInfos");
            AssertEqual(2, shopValidInfos.Count, "GetShopValidInfoResponse ShopValidInfos count");
            AssertIntegerList(
                [502, 504],
                shopValidInfos.Select(info => (long)GetRequiredIntegerMember(info, "Id")).Order().ToArray(),
                "GetShopValidInfoResponse ShopValidInfos ids");
            foreach (int shopId in new[] { 502, 504 })
            {
                object shopValidInfo = shopValidInfos.Single(info => GetRequiredIntegerMember(info, "Id") == shopId);
                AssertEqual(0, GetRequiredIntegerMember(shopValidInfo, "StartTime"), $"GetShopValidInfoResponse shop {shopId} StartTime");
                AssertEqual(0, GetRequiredIntegerMember(shopValidInfo, "EndTime"), $"GetShopValidInfoResponse shop {shopId} EndTime");
                AssertEqual(false, GetRequiredBooleanMember(shopValidInfo, "IsUnShelve"), $"GetShopValidInfoResponse shop {shopId} IsUnShelve");
                AssertObjectListEmpty(GetRequiredMemberValue(shopValidInfo, "ConditionIds"), $"GetShopValidInfoResponse shop {shopId} ConditionIds");
            }

            Type buyRequestType = RequiredShopMessageType("BuyRequest");
            Type buyResponseType = RequiredShopMessageType("BuyResponse");
            object buyRequest = Activator.CreateInstance(buyRequestType)
                ?? throw new InvalidDataException("BuyRequest: expected a public parameterless constructor for MessagePack.");
            SetRequiredIntegerMember(buyRequest, "ShopId", 1);
            SetRequiredIntegerMember(buyRequest, "GoodsId", 1003000);
            SetRequiredIntegerMember(buyRequest, "Count", 1);

            const int buyPacketId = 12_004;
            InvokeRegisteredRequestHandler(
                "BuyRequest",
                harness.Session,
                buyPacketId,
                buyRequest);
            object buyResponse = ReadResponsePayload(
                harness,
                buyPacketId,
                "BuyResponse",
                "BuyRequest shop 1 goods 1003000 response",
                buyResponseType,
                maxPacketsToRead: 4);
            AssertEqual(0, GetRequiredIntegerMember(buyResponse, "Code"), "BuyResponse Code");
            AssertEqual(false, GetRequiredBooleanMember(buyResponse, "IsShowBuyResult"), "BuyResponse IsShowBuyResult");
            List<object> goodList = GetRequiredObjectListMember(buyResponse, ["GoodList"], "BuyResponse GoodList");
            AssertEqual(1, goodList.Count, "BuyResponse GoodList count");
            object boughtReward = goodList.Single();
            AssertEqual(40103, GetRequiredIntegerMember(boughtReward, "TemplateId"), "BuyResponse GoodList[0] TemplateId");
            AssertEqual(1, GetRequiredIntegerMember(boughtReward, "Count"), "BuyResponse GoodList[0] Count");
        }

        private static void AssertSupplyShopClientShop(GetShopInfoResponse.GetShopInfoResponseClientShop clientShop, string name)
        {
            if (clientShop is null)
                throw new InvalidDataException($"{name}: expected initialized shop payload.");

            AssertEqual(1U, clientShop.Id, $"{name} Id");
            AssertEqual("Supply Shop", clientShop.Name, $"{name} Name");
            AssertEqual(true, clientShop.GoodsList.Count > 0, $"{name} GoodsList non-empty");
            GetShopInfoResponse.GetShopInfoResponseClientShop.GetShopInfoResponseClientShopGoods supplyGoods = clientShop.GoodsList.SingleOrDefault(goods => goods.Id == 1003000)
                ?? throw new InvalidDataException($"{name} GoodsList: expected goods 1003000.");
            if (supplyGoods.RewardGoods is null)
                throw new InvalidDataException($"{name} goods 1003000 RewardGoods: expected initialized reward.");
            AssertEqual(40103U, supplyGoods.RewardGoods.TemplateId, $"{name} goods 1003000 RewardGoods TemplateId");
            AssertEqual(1, supplyGoods.RewardGoods.Count, $"{name} goods 1003000 RewardGoods Count");
            GetShopInfoResponse.GetShopInfoResponseClientShop.GetShopInfoResponseClientShopGoods.GetShopInfoResponseClientShopGoodsConsume consume = supplyGoods.ConsumeList.SingleOrDefault(item => item.Id == 1)
                ?? throw new InvalidDataException($"{name} goods 1003000 ConsumeList: expected item 1.");
            AssertEqual(30000U, consume.Count, $"{name} goods 1003000 ConsumeList item 1 Count");
        }

        private static Type RequiredShopMessageType(string typeName)
        {
            return typeof(GetShopInfoRequest).Assembly.GetType($"AscNet.Common.MsgPack.{typeName}", throwOnError: false)
                ?? typeof(PacketFactory).Assembly.GetType($"AscNet.GameServer.Handlers.{typeName}", throwOnError: false)
                ?? throw new TypeLoadException($"AscNet.Common.MsgPack.{typeName}");
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

            public static MongoCollectionOverride InstallForMissingFeatureCompatibility()
            {
                return new MongoCollectionOverride(
                [
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Player)), CreateNoOpMongoCollection<AscNet.Common.Database.Player>())
                ]);
            }

            public static MongoCollectionOverride InstallForShopCompatibility()
            {
                return new MongoCollectionOverride(
                [
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Inventory)), CreateNoOpMongoCollection<AscNet.Common.Database.Inventory>()),
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Character)), CreateNoOpMongoCollection<AscNet.Common.Database.Character>()),
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Player)), CreateNoOpMongoCollection<AscNet.Common.Database.Player>())
                ]);
            }

            public static MongoCollectionOverride InstallForStoryDeployVersionGapCompatibility()
            {
                return new MongoCollectionOverride(
                [
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Inventory)), CreateNoOpMongoCollection<AscNet.Common.Database.Inventory>()),
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Character)), CreateNoOpMongoCollection<AscNet.Common.Database.Character>()),
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Player)), CreateNoOpMongoCollection<AscNet.Common.Database.Player>()),
                    (RequiredCollectionField(typeof(AscNet.Common.Database.Stage)), CreateNoOpMongoCollection<AscNet.Common.Database.Stage>())
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

        private static void ValidateMainLineLuosaitaEnterCompatibility()
        {
            MainLineLuosaitaEnterRequest request = new()
            {
                SectionId = 1
            };
            MainLineLuosaitaEnterRequest requestRoundTrip = MessagePackSerializer.Deserialize<MainLineLuosaitaEnterRequest>(
                MessagePackSerializer.Serialize(request));
            AssertEqual(1, requestRoundTrip.SectionId, "MainLineLuosaitaEnterRequest SectionId");

            MainLineLuosaitaSectionInfo initialSection = MainLineLuosaitaPayloadFactory.BuildCapturedSectionInfo();
            MainLineLuosaitaEnterResponse response = new()
            {
                Code = 0,
                SectionInfo = initialSection
            };
            MainLineLuosaitaEnterResponse roundTrip = MessagePackSerializer.Deserialize<MainLineLuosaitaEnterResponse>(
                MessagePackSerializer.Serialize(response));

            AssertEqual(0, roundTrip.Code, "MainLineLuosaitaEnterResponse Code");
            AssertLuosaitaInitialSection(roundTrip.SectionInfo, "MainLineLuosaitaEnterResponse SectionInfo");

            MainLineLuosaitaEnterRequest sectionTwoRequest = new()
            {
                SectionId = 2
            };
            MainLineLuosaitaEnterRequest sectionTwoRequestRoundTrip = MessagePackSerializer.Deserialize<MainLineLuosaitaEnterRequest>(
                MessagePackSerializer.Serialize(sectionTwoRequest));
            AssertEqual(2, sectionTwoRequestRoundTrip.SectionId, "MainLineLuosaitaEnterRequest SectionId captured section 2");

            MainLineLuosaitaEnterResponse sectionTwoResponse = new()
            {
                Code = 0,
                SectionInfo = MainLineLuosaitaPayloadFactory.BuildEnterSectionInfo(sectionTwoRequestRoundTrip.SectionId)
            };
            MainLineLuosaitaEnterResponse sectionTwoRoundTrip = MessagePackSerializer.Deserialize<MainLineLuosaitaEnterResponse>(
                MessagePackSerializer.Serialize(sectionTwoResponse));

            AssertEqual(0, sectionTwoRoundTrip.Code, "MainLineLuosaitaEnterResponse section 2 Code");
            AssertLuosaitaSectionTwoEnter(sectionTwoRoundTrip.SectionInfo, "MainLineLuosaitaEnterResponse section 2 SectionInfo");

            FubenMainLineLuosaitaData loginData = MessagePackSerializer.Deserialize<FubenMainLineLuosaitaData>(
                MessagePackSerializer.Serialize(MainLineLuosaitaPayloadFactory.BuildLoginData()));
            AssertEqual(29, loginData.IncId, "FubenMainLineLuosaitaData IncId captured login payload");
            AssertEqual(1, loginData.SectionInfos.Count, "FubenMainLineLuosaitaData SectionInfos captured login payload count");
            AssertLuosaitaInitialSection(loginData.SectionInfos[0], "FubenMainLineLuosaitaData SectionInfos[0] captured login payload");
            AssertEqual(3, loginData.KillEnemySet.Count, "FubenMainLineLuosaitaData KillEnemySet captured login payload count");
            AssertEqual(201, loginData.KillEnemySet[0], "FubenMainLineLuosaitaData KillEnemySet[0]");
            AssertEqual(202, loginData.KillEnemySet[1], "FubenMainLineLuosaitaData KillEnemySet[1]");
            AssertEqual(203, loginData.KillEnemySet[2], "FubenMainLineLuosaitaData KillEnemySet[2]");

            AssertEqual(
                true,
                MainLineLuosaitaPayloadFactory.TryBuildStageProgressSectionInfo(10380102, out MainLineLuosaitaSectionInfo stage10380102SectionInfo),
                "MainLineLuosaitaPayloadFactory stage 10380102 captured progress found");
            AssertLuosaitaStageProgressDocs(
                stage10380102SectionInfo,
                1,
                [2, 18],
                "MainLineLuosaitaPayloadFactory stage 10380102 captured progress SectionInfo");

            AssertEqual(
                true,
                MainLineLuosaitaPayloadFactory.TryBuildStageProgressSectionInfo(10380110, out MainLineLuosaitaSectionInfo stage10380110SectionInfo),
                "MainLineLuosaitaPayloadFactory stage 10380110 captured progress found");
            AssertLuosaitaStageProgressDocs(
                stage10380110SectionInfo,
                2,
                [33],
                "MainLineLuosaitaPayloadFactory stage 10380110 captured progress SectionInfo");

            AssertEqual(
                true,
                MainLineLuosaitaPayloadFactory.TryBuildStageProgressSectionInfo(17013901, out MainLineLuosaitaSectionInfo stage17013901SectionInfo),
                "MainLineLuosaitaPayloadFactory stage 17013901 captured progress found");
            AssertLuosaitaStageProgressDocs(
                stage17013901SectionInfo,
                1,
                [1],
                "MainLineLuosaitaPayloadFactory stage 17013901 captured progress SectionInfo");

            AssertFightSettleResponseCurrentClientShape();

            Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            MethodInfo buildNotifyLogin = RequiredMethod(
                accountModule,
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);

            const long resumeLoginPlayerId = 78_003;
            using (LoopbackSessionHarness resumeLoginHarness = new(
                CreateDrawCompatibilityCharacter(resumeLoginPlayerId),
                CreateDrawCompatibilityPlayer(resumeLoginPlayerId),
                CreateDrawCompatibilityInventory(resumeLoginPlayerId, []),
                "mainline-luosaita-resume-login-compat-test"))
            {
                resumeLoginHarness.Session.stage = CreateLuosaitaResumeCompatibilityStage(resumeLoginPlayerId);
                NotifyLogin resumeNotifyLogin = buildNotifyLogin.Invoke(null, [resumeLoginHarness.Session]) as NotifyLogin
                    ?? throw new InvalidDataException("AccountModule.BuildNotifyLogin returned nil or a non-NotifyLogin payload.");
                FubenMainLineLuosaitaData resumeLoginData = MessagePackSerializer.Deserialize<FubenMainLineLuosaitaData>(
                    MessagePackSerializer.Serialize(resumeNotifyLogin.FubenMainLineLuosaitaData));

                AssertEqual(1, resumeLoginData.SectionInfos.Count, "AccountModule.BuildNotifyLogin Luosaita resume SectionInfos count");
                AssertLuosaitaSectionMatchesCapturedStageProgress(
                    resumeLoginData.SectionInfos[0],
                    stage17013901SectionInfo,
                    "AccountModule.BuildNotifyLogin Luosaita resume SectionInfos[0] from cleared stage 17013901");
            }

            const long playerId = 78_001;
            const int packetId = 78_101;
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "mainline-luosaita-enter-compat-test");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
            InvokeRegisteredRequestHandler(nameof(MainLineLuosaitaEnterRequest), harness.Session, packetId, requestRoundTrip);
            MainLineLuosaitaEnterResponse handlerResponse = ReadResponsePayload<MainLineLuosaitaEnterResponse>(
                harness,
                packetId,
                nameof(MainLineLuosaitaEnterResponse),
                "MainLineLuosaitaEnterRequest response");
            AssertEqual(0, handlerResponse.Code, "MainLineLuosaitaEnterRequest handler response Code");
            AssertLuosaitaInitialSection(handlerResponse.SectionInfo, "MainLineLuosaitaEnterRequest handler response SectionInfo");

            const int sectionTwoPacketId = 78_102;
            InvokeRegisteredRequestHandler(nameof(MainLineLuosaitaEnterRequest), harness.Session, sectionTwoPacketId, sectionTwoRequestRoundTrip);
            MainLineLuosaitaEnterResponse sectionTwoHandlerResponse = ReadResponsePayload<MainLineLuosaitaEnterResponse>(
                harness,
                sectionTwoPacketId,
                nameof(MainLineLuosaitaEnterResponse),
                "MainLineLuosaitaEnterRequest section 2 response");
            AssertEqual(0, sectionTwoHandlerResponse.Code, "MainLineLuosaitaEnterRequest section 2 handler response Code");
            AssertLuosaitaSectionTwoEnter(sectionTwoHandlerResponse.SectionInfo, "MainLineLuosaitaEnterRequest section 2 handler response SectionInfo");

            const long resumeEnterPlayerId = 78_004;
            const int resumeEnterPacketId = 78_109;
            using (LoopbackSessionHarness resumeEnterHarness = new(
                CreateDrawCompatibilityCharacter(resumeEnterPlayerId),
                CreateDrawCompatibilityPlayer(resumeEnterPlayerId),
                CreateDrawCompatibilityInventory(resumeEnterPlayerId, []),
                "mainline-luosaita-resume-enter-compat-test"))
            {
                resumeEnterHarness.Session.stage = CreateLuosaitaResumeCompatibilityStage(resumeEnterPlayerId);
                InvokeRegisteredRequestHandler(nameof(MainLineLuosaitaEnterRequest), resumeEnterHarness.Session, resumeEnterPacketId, requestRoundTrip);
                MainLineLuosaitaEnterResponse resumeEnterResponse = ReadResponsePayload<MainLineLuosaitaEnterResponse>(
                    resumeEnterHarness,
                    resumeEnterPacketId,
                    nameof(MainLineLuosaitaEnterResponse),
                    "MainLineLuosaitaEnterRequest section 1 existing stage 17013901 resume response");
                AssertEqual(0, resumeEnterResponse.Code, "MainLineLuosaitaEnterRequest section 1 existing stage 17013901 resume Code");
                AssertLuosaitaSectionMatchesCapturedStageProgress(
                    resumeEnterResponse.SectionInfo,
                    stage17013901SectionInfo,
                    "MainLineLuosaitaEnterRequest section 1 existing stage 17013901 resume SectionInfo");
            }

            const int enterStoryPacketId = 78_107;
            InvokeRegisteredRequestHandler(
                nameof(EnterStoryRequest),
                harness.Session,
                enterStoryPacketId,
                new EnterStoryRequest { StageId = 10380102 });
            (NotifyMainLineLuosaitaSectionInfo enterStoryLuosaitaPush, EnterStoryResponse enterStoryResponse) =
                ReadLuosaitaEnterStoryResult(harness, enterStoryPacketId, "EnterStoryRequest stage 10380102 captured Luosaita progress");
            AssertEqual(0, enterStoryResponse.Code, "EnterStoryRequest stage 10380102 response Code");
            AssertLuosaitaStageProgressDocs(
                enterStoryLuosaitaPush.SectionInfo,
                1,
                [2],
                "EnterStoryRequest stage 10380102 NotifyMainLineLuosaitaSectionInfo SectionInfo");

            foreach ((int SectionId, int DocId, int PacketId) useDocCase in new[]
            {
                (SectionId: 1, DocId: 1, PacketId: 78_103),
                (SectionId: 2, DocId: 33, PacketId: 78_104)
            })
            {
                MainLineLuosaitaUseDocRequest useDocRequest = new()
                {
                    SectionId = useDocCase.SectionId,
                    DocId = useDocCase.DocId
                };
                MainLineLuosaitaUseDocRequest useDocRequestRoundTrip = MessagePackSerializer.Deserialize<MainLineLuosaitaUseDocRequest>(
                    MessagePackSerializer.Serialize(useDocRequest));
                AssertEqual(useDocCase.SectionId, useDocRequestRoundTrip.SectionId, $"MainLineLuosaitaUseDocRequest section {useDocCase.SectionId} DocId {useDocCase.DocId} SectionId");
                AssertEqual(useDocCase.DocId, useDocRequestRoundTrip.DocId, $"MainLineLuosaitaUseDocRequest section {useDocCase.SectionId} DocId {useDocCase.DocId} DocId");

                InvokeRegisteredRequestHandler(nameof(MainLineLuosaitaUseDocRequest), harness.Session, useDocCase.PacketId, useDocRequestRoundTrip);
                MainLineLuosaitaUseDocResponse useDocResponse = ReadResponsePayload<MainLineLuosaitaUseDocResponse>(
                    harness,
                    useDocCase.PacketId,
                    nameof(MainLineLuosaitaUseDocResponse),
                    $"MainLineLuosaitaUseDocRequest section {useDocCase.SectionId} DocId {useDocCase.DocId} response");
                AssertEqual(0, useDocResponse.Code, $"MainLineLuosaitaUseDocRequest section {useDocCase.SectionId} DocId {useDocCase.DocId} handler response Code");
                AssertLuosaitaDocUsed(useDocResponse.SectionInfo, useDocCase.SectionId, useDocCase.DocId, $"MainLineLuosaitaUseDocRequest section {useDocCase.SectionId} DocId {useDocCase.DocId} handler response SectionInfo");
            }

            foreach ((int SectionId, int PosId, int TargetPosId, int ArmyId, int ExpectedBlockId, int PacketId) moveCase in new[]
            {
                (SectionId: 1, PosId: 4, TargetPosId: 13, ArmyId: 101, ExpectedBlockId: 105, PacketId: 78_105),
                (SectionId: 2, PosId: 31, TargetPosId: 37, ArmyId: 102, ExpectedBlockId: 209, PacketId: 78_106)
            })
            {
                MainLineLuosaitaMoveRequest moveRequest = new()
                {
                    SectionId = moveCase.SectionId,
                    PosId = moveCase.PosId,
                    TargetPosId = moveCase.TargetPosId
                };
                MainLineLuosaitaMoveRequest moveRequestRoundTrip = MessagePackSerializer.Deserialize<MainLineLuosaitaMoveRequest>(
                    MessagePackSerializer.Serialize(moveRequest));
                AssertEqual(moveCase.SectionId, moveRequestRoundTrip.SectionId, $"MainLineLuosaitaMoveRequest section {moveCase.SectionId} {moveCase.PosId}->{moveCase.TargetPosId} SectionId");
                AssertEqual(moveCase.PosId, moveRequestRoundTrip.PosId, $"MainLineLuosaitaMoveRequest section {moveCase.SectionId} {moveCase.PosId}->{moveCase.TargetPosId} PosId");
                AssertEqual(moveCase.TargetPosId, moveRequestRoundTrip.TargetPosId, $"MainLineLuosaitaMoveRequest section {moveCase.SectionId} {moveCase.PosId}->{moveCase.TargetPosId} TargetPosId");

                InvokeRegisteredRequestHandler(nameof(MainLineLuosaitaMoveRequest), harness.Session, moveCase.PacketId, moveRequestRoundTrip);
                MainLineLuosaitaMoveResponse moveResponse = ReadResponsePayload<MainLineLuosaitaMoveResponse>(
                    harness,
                    moveCase.PacketId,
                    nameof(MainLineLuosaitaMoveResponse),
                    $"MainLineLuosaitaMoveRequest section {moveCase.SectionId} {moveCase.PosId}->{moveCase.TargetPosId} response");
                AssertEqual(0, moveResponse.Code, $"MainLineLuosaitaMoveRequest section {moveCase.SectionId} {moveCase.PosId}->{moveCase.TargetPosId} handler response Code");
                AssertLuosaitaArmyMoved(moveResponse.SectionInfo, moveCase.SectionId, moveCase.ArmyId, moveCase.TargetPosId, moveCase.ExpectedBlockId, $"MainLineLuosaitaMoveRequest section {moveCase.SectionId} {moveCase.PosId}->{moveCase.TargetPosId} handler response SectionInfo");
            }

            const long settlePlayerId = 78_002;
            const int fightSettlePacketId = 78_108;
            using LoopbackSessionHarness settleHarness = new(
                CreateDrawCompatibilityCharacter(settlePlayerId),
                CreateDrawCompatibilityPlayer(settlePlayerId),
                CreateDrawCompatibilityInventory(settlePlayerId, []),
                "mainline-luosaita-fight-settle-compat-test");
            settleHarness.Session.stage = CreateLoginAccountCompatibilityStage(settlePlayerId);
            InvokeRegisteredRequestHandler(
                nameof(FightSettleRequest),
                settleHarness.Session,
                fightSettlePacketId,
                CreateMissingStageSettleRequest(17013901, fightSettlePacketId, settlePlayerId));
            (NotifyMainLineLuosaitaSectionInfo fightSettleLuosaitaPush, FightSettleResponse fightSettleResponse) =
                ReadLuosaitaFightSettleResult(settleHarness, fightSettlePacketId, "FightSettleRequest stage 17013901 captured Luosaita progress");
            AssertEqual(0, fightSettleResponse.Code, "FightSettleRequest stage 17013901 response Code");
            if (fightSettleResponse.Settle is null)
                throw new InvalidDataException("FightSettleRequest stage 17013901 response: expected Settle payload.");
            AssertEqual(17013901u, fightSettleResponse.Settle.StageId, "FightSettleRequest stage 17013901 response Settle.StageId");
            AssertLuosaitaStageProgressDocs(
                fightSettleLuosaitaPush.SectionInfo,
                1,
                [1],
                "FightSettleRequest stage 17013901 NotifyMainLineLuosaitaSectionInfo SectionInfo");

            const long quickClearPlayerId = 78_005;
            const int quickClearPreFightPacketId = 78_110;
            const int quickClearFightSettlePacketId = 78_111;
            const uint quickClearInternalStageId = 17_013_901;
            const uint quickClearVisibleStageId = 10_380_101;
            const int quickClearLeftTime = 321;
            const int quickClearAchievement = 7;
            using LoopbackSessionHarness quickClearHarness = new(
                CreateDrawCompatibilityCharacter(quickClearPlayerId),
                CreateDrawCompatibilityPlayer(quickClearPlayerId),
                CreateDrawCompatibilityInventory(quickClearPlayerId, []),
                "mainline-luosaita-quick-clear-fight-settle-compat-test");
            quickClearHarness.Session.stage = CreateLoginAccountCompatibilityStage(quickClearPlayerId);
            PreFightRequest quickClearPreFightRequest = new()
            {
                PreFightData = new()
                {
                    ChallengeCount = 0,
                    StageId = quickClearInternalStageId,
                    SpeedrunStageId = quickClearVisibleStageId,
                    CardIds = [],
                    RobotIds = [],
                    FirstFightPos = 1,
                    CaptainPos = 1,
                    IsHasAssist = false
                }
            };
            PreFightRequest quickClearPreFightRoundTrip = MessagePackSerializer.Deserialize<PreFightRequest>(
                MessagePackSerializer.Serialize(quickClearPreFightRequest));
            AssertEqual(quickClearInternalStageId, quickClearPreFightRoundTrip.PreFightData.StageId, "Quick-clear PreFightRequest internal StageId MessagePack round-trip");
            AssertEqual(quickClearVisibleStageId, quickClearPreFightRoundTrip.PreFightData.SpeedrunStageId, "Quick-clear PreFightRequest visible SpeedrunStageId MessagePack round-trip");
            InvokeRegisteredRequestHandler(
                nameof(PreFightRequest),
                quickClearHarness.Session,
                quickClearPreFightPacketId,
                quickClearPreFightRoundTrip);
            PreFightResponse quickClearPreFightResponse = ReadResponsePayload<PreFightResponse>(
                quickClearHarness,
                quickClearPreFightPacketId,
                nameof(PreFightResponse),
                "Quick-clear PreFightRequest internal stage 17013901 visible stage 10380101 response");
            AssertEqual(0, quickClearPreFightResponse.Code, "Quick-clear PreFightResponse Code");
            if (quickClearPreFightResponse.FightData is null)
                throw new InvalidDataException("Quick-clear PreFightResponse: expected FightData for accepted internal Luosaita fight stage.");

            FightSettleRequest quickClearFightSettleRequest = CreateMissingStageSettleRequest(
                quickClearInternalStageId,
                quickClearPreFightResponse.FightData.FightId,
                quickClearPlayerId);
            quickClearFightSettleRequest.Result.LeftTime = quickClearLeftTime;
            quickClearFightSettleRequest.Result.AddStars = quickClearAchievement;
            InvokeRegisteredRequestHandler(
                nameof(FightSettleRequest),
                quickClearHarness.Session,
                quickClearFightSettlePacketId,
                quickClearFightSettleRequest);
            (NotifyStageData quickClearStagePush, NotifyMainLineLuosaitaSectionInfo quickClearLuosaitaPush, FightSettleResponse quickClearFightSettleResponse) =
                ReadQuickClearFightSettleResult(
                    quickClearHarness,
                    quickClearFightSettlePacketId,
                    "Quick-clear FightSettleRequest internal stage 17013901 visible stage 10380101");
            AssertEqual(0, quickClearFightSettleResponse.Code, "Quick-clear FightSettleResponse Code");
            if (quickClearFightSettleResponse.Settle is null)
                throw new InvalidDataException("Quick-clear FightSettleResponse: expected Settle payload.");
            if (quickClearStagePush.StageList.Count == 0)
                throw new InvalidDataException("Quick-clear NotifyStageData StageList: expected visible story stage update.");
            AssertEqual(quickClearVisibleStageId, (uint)quickClearStagePush.StageList[0].StageId, "Quick-clear NotifyStageData StageList[0].StageId uses visible story stage");
            AssertEqual(quickClearVisibleStageId, (uint)quickClearFightSettleResponse.Settle.StageId, "Quick-clear FightSettleResponse Settle.StageId uses visible story stage");
            AssertEqual(quickClearLeftTime, quickClearFightSettleResponse.Settle.LeftTime, "Quick-clear FightSettleResponse Settle.LeftTime comes from request Result.LeftTime");
            AssertEqual(quickClearAchievement, quickClearFightSettleResponse.Settle.Achievement, "Quick-clear FightSettleResponse Settle.Achievement comes from request Result.AddStars");
            AssertEqual(0, quickClearFightSettleResponse.Settle.ChallengeCount, "Quick-clear FightSettleResponse Settle.ChallengeCount");
            AssertLuosaitaStageProgressDocs(
                quickClearLuosaitaPush.SectionInfo,
                1,
                [1],
                "Quick-clear FightSettleRequest internal stage 17013901 NotifyMainLineLuosaitaSectionInfo SectionInfo");

            ValidateRequestHandlerRegistration(nameof(MainLineLuosaitaEnterRequest));
            ValidateRequestHandlerRegistration(nameof(MainLineLuosaitaMoveRequest));
            ValidateRequestHandlerRegistration(nameof(MainLineLuosaitaUseDocRequest));

            static void AssertFightSettleResponseCurrentClientShape()
            {
                Type settleType = typeof(FightSettleResponse.FightSettleResponseSettle);
                AssertEqual(typeof(int), MemberValueType(RequiredDataMember(settleType, nameof(FightSettleResponse.FightSettleResponseSettle.Achievement))), "FightSettleResponse.Settle Achievement retail field type");
                AssertEqual(typeof(int), MemberValueType(RequiredDataMember(settleType, nameof(FightSettleResponse.FightSettleResponseSettle.LeftTime))), "FightSettleResponse.Settle LeftTime retail field type");

                string[] nullableResultFields =
                [
                    "RiftSettleResult",
                    "SpecialTrainCubeResult",
                    "BrilliantWalkResult",
                    "Maverick2SettleResult",
                    "MazeResult",
                    "RpgSettleResult",
                    "MonsterCombatResult",
                    "TransfiniteBattleResult",
                    "TeachingActivityFightResult",
                    "PracticeFightResult",
                    "KotodamaSettleResult",
                    "BossInshotSettleResult",
                    "SucceedBossBattleResult",
                    "FpsGameSettleResult",
                    "Maverick3SettleResult",
                    "ScoreTowerSettleResult",
                    "SoloReformSettleResult",
                    "PbrFightSettleShowData"
                ];

                FightSettleResponse response = new()
                {
                    Code = 0,
                    Settle = new()
                    {
                        IsWin = true,
                        StageId = 10_380_101,
                        StarsMark = 7,
                        Achievement = 7,
                        LeftTime = 321
                    }
                };

                foreach (string field in nullableResultFields)
                    SetRequiredMemberValue(response.Settle, RequiredDataMember(settleType, field), null);

                FightSettleResponse roundTrip = MessagePackSerializer.Deserialize<FightSettleResponse>(
                    MessagePackSerializer.Serialize(response));
                if (roundTrip.Settle is null)
                    throw new InvalidDataException("FightSettleResponse.Settle current-client shape round-trip: expected Settle payload.");
                AssertEqual(7, roundTrip.Settle.Achievement, "FightSettleResponse.Settle Achievement MessagePack round-trip");
                AssertEqual(321, roundTrip.Settle.LeftTime, "FightSettleResponse.Settle LeftTime MessagePack round-trip");
                foreach (string field in nullableResultFields)
                    AssertRequiredMemberNull(roundTrip.Settle, field, $"FightSettleResponse.Settle {field} nullable MessagePack round-trip");
            }

            static void AssertLuosaitaInitialSection(MainLineLuosaitaSectionInfo? sectionInfo, string name)
            {
                if (sectionInfo is null)
                    throw new InvalidDataException($"{name}: expected captured SectionInfo payload, got nil.");

                AssertEqual(1, sectionInfo.SectionId, $"{name}.SectionId");
                AssertEqual(10, sectionInfo.BlockInfos.Count, $"{name}.BlockInfos count");
                AssertEqual(101, sectionInfo.BlockInfos[0].Id, $"{name}.BlockInfos[0].Id");
                AssertEqual(1, sectionInfo.BlockInfos[0].BlockStatus, $"{name}.BlockInfos[0].BlockStatus");
                AssertEqual(110, sectionInfo.BlockInfos[9].Id, $"{name}.BlockInfos[9].Id");
                AssertEqual(0, sectionInfo.BlockInfos[9].BlockStatus, $"{name}.BlockInfos[9].BlockStatus");

                AssertEqual(11, sectionInfo.SectionMembers.Count, $"{name}.SectionMembers count");
                MainLineLuosaitaSectionMember army = sectionInfo.SectionMembers.Single(member => member.Guid == 1);
                AssertEqual(1, army.Type, $"{name}.SectionMembers army Type");
                AssertEqual(104, army.BlockId, $"{name}.SectionMembers army BlockId");
                AssertEqual(4, army.PosId, $"{name}.SectionMembers army PosId");
                if (army.ArmyInfo is null)
                    throw new InvalidDataException($"{name}.SectionMembers army ArmyInfo: expected captured unit payload, got nil.");
                AssertEqual(101, army.ArmyInfo.Id, $"{name}.SectionMembers army ArmyInfo.Id");
                AssertEqual(8, army.ArmyInfo.CurHp, $"{name}.SectionMembers army ArmyInfo.CurHp");

                MainLineLuosaitaSectionMember stage = sectionInfo.SectionMembers.Single(member => member.Guid == 9);
                AssertEqual(4, stage.Type, $"{name}.SectionMembers stage Type");
                AssertEqual(10380101, stage.StageId, $"{name}.SectionMembers stage StageId");

                MainLineLuosaitaSectionMember enemy = sectionInfo.SectionMembers.Single(member => member.Guid == 27);
                if (enemy.EnemyInfo is null)
                    throw new InvalidDataException($"{name}.SectionMembers enemy EnemyInfo: expected captured unit payload, got nil.");
                AssertEqual(240, enemy.EnemyInfo.Id, $"{name}.SectionMembers enemy EnemyInfo.Id");
                AssertEqual(99, enemy.EnemyInfo.CurHp, $"{name}.SectionMembers enemy EnemyInfo.CurHp");

                MainLineLuosaitaSectionMember character = sectionInfo.SectionMembers.Single(member => member.Guid == 29);
                AssertEqual(3, character.Type, $"{name}.SectionMembers character Type");
                AssertEqual(3101, character.CharacterId, $"{name}.SectionMembers character CharacterId");

                AssertEqual(0, sectionInfo.DocList.Count, $"{name}.DocList count");
                AssertEqual(0, sectionInfo.CharacterMoveIds.Count, $"{name}.CharacterMoveIds count");
                AssertEqual(0, sectionInfo.SectionStatus, $"{name}.SectionStatus");
            }

            static void AssertLuosaitaSectionTwoEnter(MainLineLuosaitaSectionInfo? sectionInfo, string name)
            {
                if (sectionInfo is null)
                    throw new InvalidDataException($"{name}: expected captured section 2 SectionInfo payload, got nil.");

                AssertEqual(2, sectionInfo.SectionId, $"{name}.SectionId");
                AssertEqual(11, sectionInfo.BlockInfos.Count, $"{name}.BlockInfos count");
                AssertEqual(9, sectionInfo.SectionMembers.Count, $"{name}.SectionMembers count");
                AssertEqual(0, sectionInfo.DocList.Count, $"{name}.DocList count");
                AssertEqual(0, sectionInfo.SectionStatus, $"{name}.SectionStatus");
            }

            static void AssertLuosaitaStageProgressDocs(MainLineLuosaitaSectionInfo? sectionInfo, int expectedSectionId, int[] expectedUnusedDocIds, string name)
            {
                if (sectionInfo is null)
                    throw new InvalidDataException($"{name}: expected captured stage progress SectionInfo payload, got nil.");

                AssertEqual(expectedSectionId, sectionInfo.SectionId, $"{name}.SectionId");
                foreach (int docId in expectedUnusedDocIds)
                {
                    MainLineLuosaitaDocInfo? doc = sectionInfo.DocList.SingleOrDefault(docInfo => docInfo.Id == docId);
                    if (doc is null)
                        throw new InvalidDataException($"{name}.DocList: expected captured unlocked doc {docId}.");
                    AssertEqual(false, doc.Used, $"{name}.DocList doc {docId} Used");
                }
            }

            static void AssertLuosaitaSectionMatchesCapturedStageProgress(
                MainLineLuosaitaSectionInfo? actual,
                MainLineLuosaitaSectionInfo expected,
                string name)
            {
                if (actual is null)
                    throw new InvalidDataException($"{name}: expected derived captured stage progress SectionInfo payload, got nil.");

                AssertEqual(expected.SectionId, actual.SectionId, $"{name}.SectionId");
                AssertEqual(expected.BlockInfos.Count, actual.BlockInfos.Count, $"{name}.BlockInfos count");
                foreach (MainLineLuosaitaBlockInfo expectedBlock in expected.BlockInfos)
                {
                    MainLineLuosaitaBlockInfo? actualBlock = actual.BlockInfos.SingleOrDefault(block => block.Id == expectedBlock.Id);
                    if (actualBlock is null)
                        throw new InvalidDataException($"{name}.BlockInfos: missing captured block {expectedBlock.Id}.");
                    AssertEqual(expectedBlock.BlockStatus, actualBlock.BlockStatus, $"{name}.BlockInfos block {expectedBlock.Id} BlockStatus");
                }

                AssertEqual(expected.SectionMembers.Count, actual.SectionMembers.Count, $"{name}.SectionMembers count");
                foreach (MainLineLuosaitaSectionMember expectedMember in expected.SectionMembers)
                {
                    MainLineLuosaitaSectionMember? actualMember = actual.SectionMembers.SingleOrDefault(member => member.Guid == expectedMember.Guid);
                    if (actualMember is null)
                        throw new InvalidDataException($"{name}.SectionMembers: missing captured member {expectedMember.Guid}.");
                    AssertEqual(expectedMember.Type, actualMember.Type, $"{name}.SectionMembers member {expectedMember.Guid} Type");
                    AssertEqual(expectedMember.BlockId, actualMember.BlockId, $"{name}.SectionMembers member {expectedMember.Guid} BlockId");
                    AssertEqual(expectedMember.PosId, actualMember.PosId, $"{name}.SectionMembers member {expectedMember.Guid} PosId");
                    AssertEqual(expectedMember.CharacterId, actualMember.CharacterId, $"{name}.SectionMembers member {expectedMember.Guid} CharacterId");
                    AssertEqual(expectedMember.StageId, actualMember.StageId, $"{name}.SectionMembers member {expectedMember.Guid} StageId");
                    AssertLuosaitaUnitMatches(expectedMember.ArmyInfo, actualMember.ArmyInfo, $"{name}.SectionMembers member {expectedMember.Guid} ArmyInfo");
                    AssertLuosaitaUnitMatches(expectedMember.EnemyInfo, actualMember.EnemyInfo, $"{name}.SectionMembers member {expectedMember.Guid} EnemyInfo");
                }

                AssertEqual(expected.DocList.Count, actual.DocList.Count, $"{name}.DocList count");
                foreach (MainLineLuosaitaDocInfo expectedDoc in expected.DocList)
                {
                    MainLineLuosaitaDocInfo? actualDoc = actual.DocList.SingleOrDefault(doc => doc.Id == expectedDoc.Id);
                    if (actualDoc is null)
                        throw new InvalidDataException($"{name}.DocList: missing captured doc {expectedDoc.Id}.");
                    AssertEqual(expectedDoc.Used, actualDoc.Used, $"{name}.DocList doc {expectedDoc.Id} Used");
                }

                AssertEqual(expected.CharacterMoveIds.Count, actual.CharacterMoveIds.Count, $"{name}.CharacterMoveIds count");
                for (int index = 0; index < expected.CharacterMoveIds.Count; index++)
                    AssertEqual(expected.CharacterMoveIds[index], actual.CharacterMoveIds[index], $"{name}.CharacterMoveIds[{index}]");
                AssertEqual(expected.SectionStatus, actual.SectionStatus, $"{name}.SectionStatus");
            }

            static void AssertLuosaitaUnitMatches(MainLineLuosaitaUnitInfo? expected, MainLineLuosaitaUnitInfo? actual, string name)
            {
                if (expected is null)
                {
                    if (actual is not null)
                        throw new InvalidDataException($"{name}: expected nil captured unit payload.");
                    return;
                }

                if (actual is null)
                    throw new InvalidDataException($"{name}: expected captured unit payload, got nil.");

                AssertEqual(expected.Id, actual.Id, $"{name}.Id");
                AssertEqual(expected.CurHp, actual.CurHp, $"{name}.CurHp");
                AssertEqual(expected.ExtraAttack, actual.ExtraAttack, $"{name}.ExtraAttack");
            }

            static (NotifyMainLineLuosaitaSectionInfo LuosaitaPush, EnterStoryResponse Response) ReadLuosaitaEnterStoryResult(
                LoopbackSessionHarness harness,
                int expectedPacketId,
                string name)
            {
                NotifyMainLineLuosaitaSectionInfo? luosaitaPush = null;
                EnterStoryResponse? enterStoryResponse = null;

                for (int packetIndex = 0; packetIndex < 5 && (luosaitaPush is null || enterStoryResponse is null); packetIndex++)
                {
                    Packet packet = harness.ReadPacket(packetIndex == 0
                        ? $"{name} first packet"
                        : $"{name} packet {packetIndex + 1}");

                    switch (packet.Type)
                    {
                        case Packet.ContentType.Push:
                            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                            if (push.Name == nameof(NotifyMainLineLuosaitaSectionInfo))
                                luosaitaPush = MessagePackSerializer.Deserialize<NotifyMainLineLuosaitaSectionInfo>(push.Content);
                            break;
                        case Packet.ContentType.Response:
                            Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                            AssertEqual(expectedPacketId, response.Id, $"{name} packet id");
                            AssertEqual(nameof(EnterStoryResponse), response.Name, $"{name} packet name");
                            enterStoryResponse = MessagePackSerializer.Deserialize<EnterStoryResponse>(response.Content);
                            break;
                        default:
                            throw new InvalidDataException($"{name}: unexpected packet type {packet.Type}.");
                    }
                }

                if (luosaitaPush is null)
                    throw new InvalidDataException($"{name}: expected NotifyMainLineLuosaitaSectionInfo push.");
                if (enterStoryResponse is null)
                    throw new InvalidDataException($"{name}: expected EnterStoryResponse.");

                return (luosaitaPush, enterStoryResponse);
            }

            static (NotifyMainLineLuosaitaSectionInfo LuosaitaPush, FightSettleResponse Response) ReadLuosaitaFightSettleResult(
                LoopbackSessionHarness harness,
                int expectedPacketId,
                string name)
            {
                NotifyMainLineLuosaitaSectionInfo? luosaitaPush = null;
                FightSettleResponse? fightSettleResponse = null;

                for (int packetIndex = 0; packetIndex < 16 && (luosaitaPush is null || fightSettleResponse is null); packetIndex++)
                {
                    Packet packet = harness.ReadPacket(packetIndex == 0
                        ? $"{name} first packet"
                        : $"{name} packet {packetIndex + 1}");

                    switch (packet.Type)
                    {
                        case Packet.ContentType.Push:
                            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                            if (push.Name == nameof(NotifyMainLineLuosaitaSectionInfo))
                                luosaitaPush = MessagePackSerializer.Deserialize<NotifyMainLineLuosaitaSectionInfo>(push.Content);
                            break;
                        case Packet.ContentType.Response:
                            Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                            AssertEqual(expectedPacketId, response.Id, $"{name} packet id");
                            AssertEqual(nameof(FightSettleResponse), response.Name, $"{name} packet name");
                            fightSettleResponse = MessagePackSerializer.Deserialize<FightSettleResponse>(response.Content);
                            break;
                        default:
                            throw new InvalidDataException($"{name}: unexpected packet type {packet.Type}.");
                    }
                }

                if (luosaitaPush is null)
                    throw new InvalidDataException($"{name}: expected NotifyMainLineLuosaitaSectionInfo push.");
                if (fightSettleResponse is null)
                    throw new InvalidDataException($"{name}: expected FightSettleResponse.");

                return (luosaitaPush, fightSettleResponse);
            }

            static (NotifyStageData StagePush, NotifyMainLineLuosaitaSectionInfo LuosaitaPush, FightSettleResponse Response) ReadQuickClearFightSettleResult(
                LoopbackSessionHarness harness,
                int expectedPacketId,
                string name)
            {
                NotifyStageData? stagePush = null;
                NotifyMainLineLuosaitaSectionInfo? luosaitaPush = null;
                FightSettleResponse? fightSettleResponse = null;

                for (int packetIndex = 0; packetIndex < 16 && (stagePush is null || luosaitaPush is null || fightSettleResponse is null); packetIndex++)
                {
                    Packet packet = harness.ReadPacket(packetIndex == 0
                        ? $"{name} first packet"
                        : $"{name} packet {packetIndex + 1}");

                    switch (packet.Type)
                    {
                        case Packet.ContentType.Push:
                            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                            if (push.Name == nameof(NotifyStageData))
                                stagePush = MessagePackSerializer.Deserialize<NotifyStageData>(push.Content);
                            else if (push.Name == nameof(NotifyMainLineLuosaitaSectionInfo))
                                luosaitaPush = MessagePackSerializer.Deserialize<NotifyMainLineLuosaitaSectionInfo>(push.Content);
                            break;
                        case Packet.ContentType.Response:
                            Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                            AssertEqual(expectedPacketId, response.Id, $"{name} packet id");
                            AssertEqual(nameof(FightSettleResponse), response.Name, $"{name} packet name");
                            fightSettleResponse = MessagePackSerializer.Deserialize<FightSettleResponse>(response.Content);
                            break;
                        default:
                            throw new InvalidDataException($"{name}: unexpected packet type {packet.Type}.");
                    }
                }

                if (stagePush is null)
                    throw new InvalidDataException($"{name}: expected NotifyStageData push.");
                if (luosaitaPush is null)
                    throw new InvalidDataException($"{name}: expected NotifyMainLineLuosaitaSectionInfo push.");
                if (fightSettleResponse is null)
                    throw new InvalidDataException($"{name}: expected FightSettleResponse.");

                return (stagePush, luosaitaPush, fightSettleResponse);
            }
            static void AssertLuosaitaDocUsed(MainLineLuosaitaSectionInfo? sectionInfo, int expectedSectionId, int docId, string name)
            {
                if (sectionInfo is null)
                    throw new InvalidDataException($"{name}: expected captured UseDoc SectionInfo payload, got nil.");

                AssertEqual(expectedSectionId, sectionInfo.SectionId, $"{name}.SectionId");
                MainLineLuosaitaDocInfo? doc = sectionInfo.DocList.SingleOrDefault(docInfo => docInfo.Id == docId);
                if (doc is null)
                    throw new InvalidDataException($"{name}.DocList: expected captured used doc {docId}.");
                AssertEqual(true, doc.Used, $"{name}.DocList doc {docId} Used");
            }

            static void AssertLuosaitaArmyMoved(MainLineLuosaitaSectionInfo? sectionInfo, int expectedSectionId, int armyId, int expectedTargetPosId, int expectedBlockId, string name)
            {
                if (sectionInfo is null)
                    throw new InvalidDataException($"{name}: expected captured Move SectionInfo payload, got nil.");

                AssertEqual(expectedSectionId, sectionInfo.SectionId, $"{name}.SectionId");
                MainLineLuosaitaSectionMember army = sectionInfo.SectionMembers.Single(member => member.Type == 1 && member.ArmyInfo?.Id == armyId);
                AssertEqual(expectedTargetPosId, army.PosId, $"{name}.SectionMembers army {armyId} PosId");
                AssertEqual(expectedBlockId, army.BlockId, $"{name}.SectionMembers army {armyId} BlockId");
            }

            static AscNet.Common.Database.Stage CreateLuosaitaResumeCompatibilityStage(long uid)
            {
                AscNet.Common.Database.Stage stage = CreateLoginAccountCompatibilityStage(uid);
                stage.AddStage(new StageDatum
                {
                    StageId = 17013901,
                    StarsMark = 7,
                    Passed = true,
                    PassTimesToday = 0,
                    PassTimesTotal = 1
                });
                return stage;
            }
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

        private static void ValidateGatherAwakenRewardCompatibility()
        {
            const int luciaInverseCrownCharacterId = 1021007;
            const int level2ExhibitionRewardId = 402;
            const int level3ExhibitionRewardId = 403;
            const int level4ExhibitionRewardId = 404;
            const int level2RewardId = 10210070;
            const int level3RewardId = 10210071;
            const int level4RewardId = 10210072;
            const int level4SkillGroupId = 1027270;
            const int level2RewardGoodsId = 102100700;
            const int level3RewardGoodsId = 102100710;
            const int level4CoreRewardGoodsId = 102100720;
            const int level4ShardRewardGoodsId = 102100721;
            const int level2FashionId = 6006102;
            const int level3FashionId = 6006103;
            const string name = "Lucia: Inverse Crown awaken GatherReward compatibility";

            Dictionary<int, ExhibitionRewardTable> exhibitionRewardRowsById = TableReaderV2.Parse<ExhibitionRewardTable>()
                .Where(reward => reward.CharacterId == luciaInverseCrownCharacterId)
                .ToDictionary(reward => reward.Id);

            AssertIntegerList(
                [401, 402, 403, 404, 405],
                exhibitionRewardRowsById.Keys.Order().Select(id => (long)id).ToArray(),
                $"{name} ExhibitionReward row ids");

            for (int level = 1; level <= 5; level++)
            {
                int exhibitionRewardId = 400 + level;
                ExhibitionRewardTable exhibitionReward = exhibitionRewardRowsById.TryGetValue(exhibitionRewardId, out ExhibitionRewardTable? row)
                    ? row
                    : throw new InvalidDataException($"{name}: missing ExhibitionReward row {exhibitionRewardId}.");
                AssertEqual(luciaInverseCrownCharacterId, exhibitionReward.CharacterId, $"{name} ExhibitionReward {exhibitionRewardId} CharacterId");
                AssertEqual(level, exhibitionReward.LevelId, $"{name} ExhibitionReward {exhibitionRewardId} LevelId");
            }

            ExhibitionRewardTable level2Reward = exhibitionRewardRowsById[level2ExhibitionRewardId];
            ExhibitionRewardTable level3Reward = exhibitionRewardRowsById[level3ExhibitionRewardId];
            ExhibitionRewardTable level4Reward = exhibitionRewardRowsById[level4ExhibitionRewardId];
            AssertEqual(level2RewardId, level2Reward.RewardId, $"{name} level 2 RewardId");
            AssertEqual(level3RewardId, level3Reward.RewardId, $"{name} level 3 RewardId");
            AssertEqual(level4RewardId, level4Reward.RewardId, $"{name} level 4 RewardId");
            AssertEqual(level4SkillGroupId, level4Reward.SkillGroupId, $"{name} level 4 SkillGroupId");

            List<RewardGoodsTable> rewardGoodsRows = TableReaderV2.Parse<RewardGoodsTable>();
            List<RewardGoodsTable> level2RewardGoods = ResolveRewardGoods(level2RewardId, rewardGoodsRows, $"{name} level 2 RewardId");
            List<RewardGoodsTable> level3RewardGoods = ResolveRewardGoods(level3RewardId, rewardGoodsRows, $"{name} level 3 RewardId");
            List<RewardGoodsTable> level4RewardGoods = ResolveRewardGoods(level4RewardId, rewardGoodsRows, $"{name} level 4 RewardId");

            AssertIntegerList(
                [level2RewardGoodsId],
                level2RewardGoods.Select(goods => (long)goods.Id).ToArray(),
                $"{name} level 2 RewardGoods ids");
            AssertIntegerList(
                [level3RewardGoodsId],
                level3RewardGoods.Select(goods => (long)goods.Id).ToArray(),
                $"{name} level 3 RewardGoods ids");
            AssertIntegerList(
                [level4CoreRewardGoodsId, level4ShardRewardGoodsId],
                level4RewardGoods.Select(goods => (long)goods.Id).ToArray(),
                $"{name} level 4 RewardGoods ids");

            AssertAwakenFashionRewardGoods(level2RewardGoods.Single(), level2FashionId, $"{name} level 2 fashion grant");
            AssertAwakenFashionRewardGoods(level3RewardGoods.Single(), level3FashionId, $"{name} level 3 fashion grant");
            AssertGatherAwakenFashionRewardGrant(
                level2ExhibitionRewardId,
                level2RewardGoodsId,
                level2FashionId,
                initialFashionIsLocked: false,
                $"{name} level 2 missing fashion grant");
            AssertGatherAwakenFashionRewardGrant(
                level2ExhibitionRewardId,
                level2RewardGoodsId,
                level2FashionId,
                initialFashionIsLocked: true,
                $"{name} level 2 locked fashion unlock");
            AssertClaimedGatherAwakenFashionRewardLoginRepair(
                luciaInverseCrownCharacterId,
                level2ExhibitionRewardId,
                level2FashionId,
                initialFashionIsLocked: false,
                $"{name} claimed level 2 missing fashion login repair");
            AssertClaimedGatherAwakenFashionRewardLoginRepair(
                luciaInverseCrownCharacterId,
                level2ExhibitionRewardId,
                level2FashionId,
                initialFashionIsLocked: true,
                $"{name} claimed level 2 locked fashion login repair");
        }

        private static void AssertAwakenFashionRewardGoods(RewardGoodsTable rewardGoods, int expectedFashionId, string name)
        {
            AssertEqual(expectedFashionId, rewardGoods.TemplateId, $"{name} TemplateId");
            AssertEqual(1, rewardGoods.Count, $"{name} Count");
        }

        private static void AssertGatherAwakenFashionRewardGrant(
            int exhibitionRewardId,
            int expectedRewardGoodsId,
            int expectedFashionId,
            bool initialFashionIsLocked,
            string name)
        {
            const long playerId = 102_100_702;
            const int packetId = 102_100_702;

            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            if (initialFashionIsLocked)
            {
                character.Fashions.Add(new FashionList
                {
                    Id = expectedFashionId,
                    IsLock = true
                });
                AssertEqual(true, character.Fashions.Single(fashion => fashion.Id == expectedFashionId).IsLock, $"{name} precondition locked fashion");
            }
            else if (character.Fashions.Any(fashion => fashion.Id == expectedFashionId))
            {
                throw new InvalidDataException($"{name}: expected fashion {expectedFashionId} to be absent before reward.");
            }

            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            using LoopbackSessionHarness harness = new(
                character,
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                $"{name.Replace(' ', '-').ToLowerInvariant()}-test");

            InvokeRegisteredRequestHandler(
                nameof(GatherRewardRequest),
                harness.Session,
                packetId,
                new GatherRewardRequest { Id = exhibitionRewardId });

            FashionList persistedFashion = character.Fashions.Single(fashion => fashion.Id == expectedFashionId);
            AssertEqual(false, persistedFashion.IsLock, $"{name} persisted fashion unlocked");

            FashionSyncNotify fashionSync = ReadPushPayload<FashionSyncNotify>(
                harness,
                nameof(FashionSyncNotify),
                $"{name} FashionSyncNotify");
            FashionList notifiedFashion = fashionSync.FashionList.Single(fashion => fashion.Id == expectedFashionId);
            AssertEqual(false, notifiedFashion.IsLock, $"{name} notified fashion unlocked");

            NotifyGatherReward gatherRewardPush = ReadPushPayload<NotifyGatherReward>(
                harness,
                nameof(NotifyGatherReward),
                $"{name} NotifyGatherReward");
            AssertEqual(exhibitionRewardId, gatherRewardPush.Id, $"{name} claimed gather reward id");

            GatherRewardResponse response = ReadResponsePayload<GatherRewardResponse>(
                harness,
                packetId,
                nameof(GatherRewardResponse),
                $"{name} GatherRewardResponse");
            AssertEqual(0, response.Code, $"{name} response code");
            RewardGoods rewardGoods = response.RewardGoods.Single(goods => goods.Id == expectedRewardGoodsId);
            AssertEqual(expectedFashionId, rewardGoods.TemplateId, $"{name} response RewardGoods TemplateId");
            AssertEqual(1, rewardGoods.Count, $"{name} response RewardGoods Count");
            AssertEqual((int)RewardType.Fashion, rewardGoods.RewardType, $"{name} response RewardGoods type");
        }

        private static void AssertClaimedGatherAwakenFashionRewardLoginRepair(
            int characterId,
            int exhibitionRewardId,
            int expectedFashionId,
            bool initialFashionIsLocked,
            string name)
        {
            long playerId = initialFashionIsLocked ? 102_100_704 : 102_100_703;
            const int defaultFashionId = 6006101;

            Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            MethodInfo doLogin = RequiredMethod(
                accountModule,
                "DoLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);

            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.GatherRewards = [exhibitionRewardId];
            player.PlayerData.LastLoginTime = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();

            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter((uint)characterId, defaultFashionId));
            character.Fashions.Add(new FashionList
            {
                Id = defaultFashionId,
                IsLock = false
            });
            if (initialFashionIsLocked)
            {
                character.Fashions.Add(new FashionList
                {
                    Id = expectedFashionId,
                    IsLock = true
                });
                AssertEqual(true, character.Fashions.Single(fashion => fashion.Id == expectedFashionId).IsLock, $"{name} precondition locked fashion");
            }
            else if (character.Fashions.Any(fashion => fashion.Id == expectedFashionId))
            {
                throw new InvalidDataException($"{name}: expected fashion {expectedFashionId} to be absent before login repair.");
            }

            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();
            using LoopbackSessionHarness harness = new(
                character,
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                $"{name.Replace(' ', '-').ToLowerInvariant()}-test");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

            doLogin.Invoke(null, [harness.Session]);

            FashionList persistedFashion = character.Fashions.Single(fashion => fashion.Id == expectedFashionId);
            AssertEqual(false, persistedFashion.IsLock, $"{name} persisted fashion unlocked");

            NotifyLogin startupLogin = ReadPushPayload<NotifyLogin>(
                harness,
                nameof(NotifyLogin),
                $"{name} AccountModule.DoLogin NotifyLogin startup payload");
            FashionList loginFashion = startupLogin.FashionList.Single(fashion => fashion.Id == expectedFashionId);
            AssertEqual(false, loginFashion.IsLock, $"{name} NotifyLogin FashionList unlocked fashion");
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
                new("CharacterEnhanceSkillNoticeRequest", "CharacterEnhanceSkillNoticeRequestHandler"),
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

        private static void ValidateCharacterSkillGroupTableBackedCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();

            const int veronicaAegisCharacterId = 1381003;
            const int skillGroupId = 1383010;
            const uint skillId = 138301;
            const int unlockPacketId = 16_001;
            const int upgradePacketId = 16_002;
            const long unlockPlayerId = 88_009;
            const long upgradePlayerId = 88_010;
            const int initialSkillLevel = 1;
            const int upgradeCount = 2;
            const long initialCoinCount = 999_999;
            const long initialSkillPointCount = 999;

            (int expectedCoinCost, int expectedSkillPointCost) = AssertTableBackedSkillGroupCompatibilityFixture(
                veronicaAegisCharacterId,
                skillGroupId,
                skillId,
                initialSkillLevel,
                upgradeCount,
                "Veronica: Aegis table-backed CharacterSkillGroup compatibility fixture");

            AscNet.Common.Database.Character unlockRoster = CreateTestCharacterRoster(veronicaAegisCharacterId, level: 1);
            unlockRoster.Uid = unlockPlayerId;
            CharacterData unlockCharacter = RequiredCharacterData(unlockRoster, veronicaAegisCharacterId);
            int removedSkills = unlockCharacter.SkillList.RemoveAll(skill => skill.Id == skillId);
            AssertEqual(1, removedSkills, "Character.AddCharacter initial table-backed skill fixture");

            using (LoopbackSessionHarness harness = new(
                unlockRoster,
                CreateDrawCompatibilityPlayer(unlockPlayerId),
                CreateDrawCompatibilityInventory(unlockPlayerId, []),
                "character-skill-group-unlock-table-backed-compat-test"))
            {
                InvokeRegisteredRequestHandler(
                    "CharacterUnlockSkillGroupRequest",
                    harness.Session,
                    unlockPacketId,
                    new AscNet.GameServer.Handlers.CharacterUnlockSkillGroupRequest
                    {
                        SkillGroupId = skillGroupId
                    });

                NotifyCharacterDataList unlockNotify = ReadPushPayload<NotifyCharacterDataList>(
                    harness,
                    nameof(NotifyCharacterDataList),
                    "CharacterUnlockSkillGroupRequest table-backed NotifyCharacterDataList");
                CharacterData pushedUnlockCharacter = RequiredNotifyCharacterData(
                    unlockNotify,
                    veronicaAegisCharacterId,
                    "CharacterUnlockSkillGroupRequest table-backed notify");
                CharacterSkill unlockedSkill = RequiredCharacterSkill(
                    pushedUnlockCharacter,
                    skillId,
                    "CharacterUnlockSkillGroupRequest table-backed skill");
                AssertEqual(initialSkillLevel, unlockedSkill.Level, "CharacterUnlockSkillGroupRequest table-backed skill initial level");
            }

            AscNet.Common.Database.Character upgradeRoster = CreateTestCharacterRoster(veronicaAegisCharacterId, level: 1);
            upgradeRoster.Uid = upgradePlayerId;
            CharacterSkill upgradeSkill = RequiredCharacterSkill(
                RequiredCharacterData(upgradeRoster, veronicaAegisCharacterId),
                skillId,
                "CharacterUpgradeSkillGroupRequest table-backed setup skill");
            upgradeSkill.Level = initialSkillLevel;

            using (LoopbackSessionHarness harness = new(
                upgradeRoster,
                CreateDrawCompatibilityPlayer(upgradePlayerId),
                CreateDrawCompatibilityInventory(
                    upgradePlayerId,
                    [
                        new Item { Id = AscNet.Common.Database.Inventory.Coin, Count = initialCoinCount },
                        new Item { Id = AscNet.Common.Database.Inventory.SkillPoint, Count = initialSkillPointCount }
                    ]),
                "character-skill-group-upgrade-table-backed-compat-test"))
            {
                InvokeRegisteredRequestHandler(
                    "CharacterUpgradeSkillGroupRequest",
                    harness.Session,
                    upgradePacketId,
                    new CharacterUpgradeSkillGroupRequest
                    {
                        SkillGroupId = skillGroupId,
                        Count = upgradeCount
                    });

                NotifyCharacterDataList upgradeNotify = ReadPushPayload<NotifyCharacterDataList>(
                    harness,
                    nameof(NotifyCharacterDataList),
                    "CharacterUpgradeSkillGroupRequest table-backed NotifyCharacterDataList");
                CharacterData pushedUpgradeCharacter = RequiredNotifyCharacterData(
                    upgradeNotify,
                    veronicaAegisCharacterId,
                    "CharacterUpgradeSkillGroupRequest table-backed notify");
                CharacterSkill upgradedSkill = RequiredCharacterSkill(
                    pushedUpgradeCharacter,
                    skillId,
                    "CharacterUpgradeSkillGroupRequest table-backed skill");
                int expectedSkillLevel = initialSkillLevel + upgradeCount;
                AssertEqual(expectedSkillLevel, upgradedSkill.Level, "CharacterUpgradeSkillGroupRequest table-backed pushed skill level");

                NotifyItemDataList upgradeItemNotify = ReadPushPayload<NotifyItemDataList>(
                    harness,
                    nameof(NotifyItemDataList),
                    "CharacterUpgradeSkillGroupRequest table-backed NotifyItemDataList");
                AssertEqual(2, upgradeItemNotify.ItemDataList.Count, "CharacterUpgradeSkillGroupRequest table-backed notified item count");
                Item pushedCoin = upgradeItemNotify.ItemDataList.Single(item => item.Id == AscNet.Common.Database.Inventory.Coin);
                Item pushedSkillPoint = upgradeItemNotify.ItemDataList.Single(item => item.Id == AscNet.Common.Database.Inventory.SkillPoint);
                long expectedCoinCount = initialCoinCount - expectedCoinCost;
                long expectedSkillPointCount = initialSkillPointCount - expectedSkillPointCost;
                AssertEqual(expectedCoinCount, pushedCoin.Count, "CharacterUpgradeSkillGroupRequest table-backed NotifyItemDataList coin count");
                AssertEqual(expectedSkillPointCount, pushedSkillPoint.Count, "CharacterUpgradeSkillGroupRequest table-backed NotifyItemDataList skill point count");

                CharacterUpgradeSkillGroupResponse upgradeResponse = (CharacterUpgradeSkillGroupResponse)ReadResponsePayload(
                    harness,
                    upgradePacketId,
                    nameof(CharacterUpgradeSkillGroupResponse),
                    "CharacterUpgradeSkillGroupRequest table-backed response",
                    typeof(CharacterUpgradeSkillGroupResponse),
                    maxPacketsToRead: 1);
                AssertEqual(upgradedSkill.Level, upgradeResponse.Level, "CharacterUpgradeSkillGroupResponse table-backed Level matches pushed skill level");

                Item sessionCoin = harness.Session.inventory.Items.Single(item => item.Id == AscNet.Common.Database.Inventory.Coin);
                Item sessionSkillPoint = harness.Session.inventory.Items.Single(item => item.Id == AscNet.Common.Database.Inventory.SkillPoint);
                AssertEqual(expectedCoinCount, sessionCoin.Count, "CharacterUpgradeSkillGroupRequest table-backed session inventory coin count");
                AssertEqual(expectedSkillPointCount, sessionSkillPoint.Count, "CharacterUpgradeSkillGroupRequest table-backed session inventory skill point count");
            }

            static (int CoinCost, int SkillPointCost) AssertTableBackedSkillGroupCompatibilityFixture(
                int characterId,
                int skillGroupId,
                uint expectedSkillId,
                int startLevel,
                int upgradeCount,
                string name)
            {
                CharacterSkillTable characterSkill = TableReaderV2.Parse<CharacterSkillTable>()
                    .SingleOrDefault(skill => skill.CharacterId == characterId)
                    ?? throw new InvalidDataException($"{name}: CharacterSkill.tsv is missing character {characterId}.");
                if (!characterSkill.SkillGroupId.Contains(skillGroupId))
                    throw new InvalidDataException($"{name}: CharacterSkill.tsv character {characterId} does not reference SkillGroupId {skillGroupId}.");

                List<CharacterSkillGroupTable> skillGroupRows = TableReaderV2.Parse<CharacterSkillGroupTable>()
                    .Where(skillGroup => skillGroup.Id == skillGroupId)
                    .ToList();
                AssertEqual(1, skillGroupRows.Count, $"{name} CharacterSkillGroup.tsv row count");
                CharacterSkillGroupTable skillGroup = skillGroupRows[0];
                if (!skillGroup.SkillId.Contains((int)expectedSkillId))
                    throw new InvalidDataException($"{name}: CharacterSkillGroup.tsv SkillGroupId {skillGroupId} does not map to SkillId {expectedSkillId}.");

                CharacterSkillUpgradeTable[] upgradeRows = Enumerable.Range(startLevel, upgradeCount)
                    .Select(level => TableReaderV2.Parse<CharacterSkillUpgradeTable>()
                        .SingleOrDefault(upgrade => upgrade.SkillId == expectedSkillId && upgrade.Level == level)
                        ?? throw new InvalidDataException($"{name}: CharacterSkillUpgrade.tsv is missing SkillId {expectedSkillId} Level {level}."))
                    .ToArray();
                int coinCost = upgradeRows.Sum(upgrade => upgrade.UseCoin ?? 0);
                int skillPointCost = upgradeRows.Sum(upgrade => upgrade.UseSkillPoint ?? 0);
                if (coinCost <= 0)
                    throw new InvalidDataException($"{name}: CharacterSkillUpgrade.tsv SkillId {expectedSkillId} levels {startLevel}..{startLevel + upgradeCount - 1} must consume cogs.");
                if (skillPointCost <= 0)
                    throw new InvalidDataException($"{name}: CharacterSkillUpgrade.tsv SkillId {expectedSkillId} levels {startLevel}..{startLevel + upgradeCount - 1} must consume skill points.");
                return (coinCost, skillPointCost);
            }

            static CharacterData RequiredNotifyCharacterData(NotifyCharacterDataList notify, int characterId, string name)
            {
                List<CharacterData> matches = notify.CharacterDataList
                    .Where(character => character.Id == characterId)
                    .ToList();
                AssertEqual(1, matches.Count, $"{name} affected character count");
                return matches[0];
            }

            static CharacterSkill RequiredCharacterSkill(CharacterData character, uint skillId, string name)
            {
                List<CharacterSkill> matches = character.SkillList
                    .Where(skill => skill.Id == skillId)
                    .ToList();
                AssertEqual(1, matches.Count, $"{name} skill count");
                return matches[0];
            }
        }

        private static void ValidateCharacterEnhanceSkillTableBackedCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();

            const int veronicaAegisCharacterId = 1381003;
            const int targetSkillGroupId = 1383280;
            const uint targetSkillId = 138328;
            const int unlockPacketId = 16_011;
            const int upgradePacketId = 16_012;
            const long unlockPlayerId = 88_011;
            const long upgradePlayerId = 88_012;
            const long costSurplus = 10;

            (Dictionary<int, int> unlockCosts, Dictionary<int, int> upgradeCosts) = AssertTableBackedEnhanceSkillCompatibilityFixture(
                veronicaAegisCharacterId,
                [1383280, 1383290, 1383300],
                targetSkillGroupId,
                targetSkillId,
                "Veronica: Aegis table-backed CharacterEnhanceSkill compatibility fixture");

            AscNet.Common.Database.Character unlockRoster = CreateTestCharacterRoster(veronicaAegisCharacterId, level: 1);
            unlockRoster.Uid = unlockPlayerId;
            CharacterData unlockCharacter = RequiredCharacterData(unlockRoster, veronicaAegisCharacterId);
            unlockCharacter.EnhanceSkillList.RemoveAll(skill => skill.Id == targetSkillId);

            Dictionary<int, long> unlockInitialCounts = InitialCountsForCosts(unlockCosts, costSurplus);
            AscNet.Common.Database.Inventory unlockInventory = CreateInventoryWithCosts(unlockPlayerId, unlockInitialCounts);
            using (LoopbackSessionHarness harness = new(
                unlockRoster,
                CreateDrawCompatibilityPlayer(unlockPlayerId),
                unlockInventory,
                "character-enhance-skill-unlock-table-backed-compat-test"))
            {
                InvokeRegisteredRequestHandler(
                    "CharacterUnlockEnhanceSkillRequest",
                    harness.Session,
                    unlockPacketId,
                    new CharacterUnlockEnhanceSkillRequest
                    {
                        SkillGroupId = targetSkillGroupId
                    });

                NotifyItemDataList unlockItemNotify = ReadPushPayload<NotifyItemDataList>(
                    harness,
                    nameof(NotifyItemDataList),
                    "CharacterUnlockEnhanceSkillRequest table-backed NotifyItemDataList");
                AssertCostConsumption(
                    unlockItemNotify,
                    harness.Session.inventory,
                    unlockInitialCounts,
                    unlockCosts,
                    "CharacterUnlockEnhanceSkillRequest table-backed level 0 unlock costs");

                NotifyCharacterDataList unlockCharacterNotify = ReadPushPayload<NotifyCharacterDataList>(
                    harness,
                    nameof(NotifyCharacterDataList),
                    "CharacterUnlockEnhanceSkillRequest table-backed NotifyCharacterDataList");
                CharacterData pushedUnlockCharacter = RequiredNotifyCharacterData(
                    unlockCharacterNotify,
                    veronicaAegisCharacterId,
                    "CharacterUnlockEnhanceSkillRequest table-backed notify");
                CharacterSkill unlockedSkill = RequiredEnhanceSkill(
                    pushedUnlockCharacter,
                    targetSkillId,
                    "CharacterUnlockEnhanceSkillRequest table-backed skill");
                AssertEqual(1, unlockedSkill.Level, "CharacterUnlockEnhanceSkillRequest table-backed pushed skill level");

                CharacterUnlockEnhanceSkillResponse unlockResponse = ReadResponsePayload<CharacterUnlockEnhanceSkillResponse>(
                    harness,
                    unlockPacketId,
                    nameof(CharacterUnlockEnhanceSkillResponse),
                    "CharacterUnlockEnhanceSkillRequest table-backed response");
                AssertEqual(0, unlockResponse.Code, "CharacterUnlockEnhanceSkillResponse table-backed Code");
            }

            AscNet.Common.Database.Character upgradeRoster = CreateTestCharacterRoster(veronicaAegisCharacterId, level: 1);
            upgradeRoster.Uid = upgradePlayerId;
            CharacterData upgradeCharacter = RequiredCharacterData(upgradeRoster, veronicaAegisCharacterId);
            upgradeCharacter.EnhanceSkillList.RemoveAll(skill => skill.Id == targetSkillId);
            upgradeCharacter.EnhanceSkillList.Add(new CharacterSkill
            {
                Id = targetSkillId,
                Level = 1
            });

            Dictionary<int, long> upgradeInitialCounts = InitialCountsForCosts(upgradeCosts, costSurplus);
            AscNet.Common.Database.Inventory upgradeInventory = CreateInventoryWithCosts(upgradePlayerId, upgradeInitialCounts);
            using (LoopbackSessionHarness harness = new(
                upgradeRoster,
                CreateDrawCompatibilityPlayer(upgradePlayerId),
                upgradeInventory,
                "character-enhance-skill-upgrade-table-backed-compat-test"))
            {
                InvokeRegisteredRequestHandler(
                    "CharacterUpgradeEnhanceSkillRequest",
                    harness.Session,
                    upgradePacketId,
                    new CharacterUpgradeEnhanceSkillRequest
                    {
                        SkillGroupId = targetSkillGroupId,
                        Count = 1
                    });

                NotifyItemDataList upgradeItemNotify = ReadPushPayload<NotifyItemDataList>(
                    harness,
                    nameof(NotifyItemDataList),
                    "CharacterUpgradeEnhanceSkillRequest table-backed NotifyItemDataList");
                AssertCostConsumption(
                    upgradeItemNotify,
                    harness.Session.inventory,
                    upgradeInitialCounts,
                    upgradeCosts,
                    "CharacterUpgradeEnhanceSkillRequest table-backed level 1 upgrade costs");

                NotifyCharacterDataList upgradeCharacterNotify = ReadPushPayload<NotifyCharacterDataList>(
                    harness,
                    nameof(NotifyCharacterDataList),
                    "CharacterUpgradeEnhanceSkillRequest table-backed NotifyCharacterDataList");
                CharacterData pushedUpgradeCharacter = RequiredNotifyCharacterData(
                    upgradeCharacterNotify,
                    veronicaAegisCharacterId,
                    "CharacterUpgradeEnhanceSkillRequest table-backed notify");
                CharacterSkill upgradedSkill = RequiredEnhanceSkill(
                    pushedUpgradeCharacter,
                    targetSkillId,
                    "CharacterUpgradeEnhanceSkillRequest table-backed skill");
                AssertEqual(2, upgradedSkill.Level, "CharacterUpgradeEnhanceSkillRequest table-backed pushed skill level");

                CharacterUpgradeEnhanceSkillResponse upgradeResponse = ReadResponsePayload<CharacterUpgradeEnhanceSkillResponse>(
                    harness,
                    upgradePacketId,
                    nameof(CharacterUpgradeEnhanceSkillResponse),
                    "CharacterUpgradeEnhanceSkillRequest table-backed response");
                AssertEqual(0, upgradeResponse.Code, "CharacterUpgradeEnhanceSkillResponse table-backed Code");
            }

            static (Dictionary<int, int> UnlockCosts, Dictionary<int, int> UpgradeCosts) AssertTableBackedEnhanceSkillCompatibilityFixture(
                int characterId,
                int[] expectedSkillGroupIds,
                int targetSkillGroupId,
                uint targetSkillId,
                string name)
            {
                EnhanceSkillTable enhanceSkill = TableReaderV2.Parse<EnhanceSkillTable>()
                    .SingleOrDefault(skill => skill.CharacterId == characterId)
                    ?? throw new InvalidDataException($"{name}: EnhanceSkill.tsv is missing character {characterId}.");
                AssertIntegerList(
                    expectedSkillGroupIds.Select(skillGroupId => (long)skillGroupId).ToArray(),
                    enhanceSkill.SkillGroupId.Where(skillGroupId => skillGroupId > 0).Select(skillGroupId => (long)skillGroupId).ToArray(),
                    $"{name} EnhanceSkill.tsv SkillGroupId");

                EnhanceSkillGroupTable enhanceSkillGroup = TableReaderV2.Parse<EnhanceSkillGroupTable>()
                    .SingleOrDefault(skillGroup => skillGroup.Id == targetSkillGroupId)
                    ?? throw new InvalidDataException($"{name}: EnhanceSkillGroup.tsv is missing SkillGroupId {targetSkillGroupId}.");
                AssertIntegerList(
                    [(long)targetSkillId],
                    enhanceSkillGroup.SkillId.Where(skillId => skillId > 0).Select(skillId => (long)skillId).ToArray(),
                    $"{name} EnhanceSkillGroup.tsv SkillId");

                return (
                    RequiredEnhanceSkillCosts(targetSkillId, level: 0, $"{name} EnhanceSkillUpgrade.tsv level 0 unlock"),
                    RequiredEnhanceSkillCosts(targetSkillId, level: 1, $"{name} EnhanceSkillUpgrade.tsv level 1 upgrade"));
            }

            static Dictionary<int, int> RequiredEnhanceSkillCosts(uint skillId, int level, string name)
            {
                EnhanceSkillUpgradeTable upgrade = TableReaderV2.Parse<EnhanceSkillUpgradeTable>()
                    .SingleOrDefault(upgrade => upgrade.SkillId == skillId && upgrade.Level == level)
                    ?? throw new InvalidDataException($"{name}: missing SkillId {skillId} Level {level}.");
                Dictionary<int, int> costs = AggregateCosts(upgrade.CostItem, upgrade.CostItemCount, name);
                if (costs.Values.Sum() <= 0)
                    throw new InvalidDataException($"{name}: expected positive item costs.");
                return costs;
            }

            static Dictionary<int, int> AggregateCosts(IReadOnlyList<int> itemIds, IReadOnlyList<int> itemCounts, string name)
            {
                Dictionary<int, int> costs = [];
                int pairCount = Math.Min(itemIds.Count, itemCounts.Count);
                for (int index = 0; index < pairCount; index++)
                {
                    int itemId = itemIds[index];
                    int itemCount = itemCounts[index];
                    if (itemId <= 0 && itemCount <= 0)
                        continue;
                    if (itemId <= 0)
                        throw new InvalidDataException($"{name}: cost entry {index + 1} has non-positive item id {itemId}.");
                    if (itemCount <= 0)
                        throw new InvalidDataException($"{name}: cost entry {index + 1} for item {itemId} has non-positive count {itemCount}.");

                    costs[itemId] = costs.TryGetValue(itemId, out int existingCount)
                        ? existingCount + itemCount
                        : itemCount;
                }

                if (costs.Count == 0)
                    throw new InvalidDataException($"{name}: expected at least one cost item.");
                return costs;
            }

            static Dictionary<int, long> InitialCountsForCosts(IReadOnlyDictionary<int, int> costs, long surplus)
            {
                return costs.ToDictionary(cost => cost.Key, cost => (long)cost.Value + surplus);
            }

            static AscNet.Common.Database.Inventory CreateInventoryWithCosts(long playerId, IReadOnlyDictionary<int, long> initialCounts)
            {
                return CreateDrawCompatibilityInventory(
                    playerId,
                    initialCounts
                        .OrderBy(count => count.Key)
                        .Select(count => new Item { Id = count.Key, Count = count.Value }));
            }

            static void AssertCostConsumption(
                NotifyItemDataList notify,
                AscNet.Common.Database.Inventory inventory,
                IReadOnlyDictionary<int, long> initialCounts,
                IReadOnlyDictionary<int, int> costs,
                string name)
            {
                AssertEqual(costs.Count, notify.ItemDataList.Count, $"{name} NotifyItemDataList cost item count");
                foreach (KeyValuePair<int, int> cost in costs.OrderBy(cost => cost.Key))
                {
                    long expectedCount = initialCounts[cost.Key] - cost.Value;
                    Item pushedItem = notify.ItemDataList.Single(item => item.Id == cost.Key);
                    AssertEqual(expectedCount, pushedItem.Count, $"{name} NotifyItemDataList item {cost.Key} count");

                    Item sessionItem = inventory.Items.Single(item => item.Id == cost.Key);
                    AssertEqual(expectedCount, sessionItem.Count, $"{name} session inventory item {cost.Key} count");
                }
            }

            static CharacterData RequiredNotifyCharacterData(NotifyCharacterDataList notify, int characterId, string name)
            {
                List<CharacterData> matches = notify.CharacterDataList
                    .Where(character => character.Id == characterId)
                    .ToList();
                AssertEqual(1, matches.Count, $"{name} affected character count");
                return matches[0];
            }

            static CharacterSkill RequiredEnhanceSkill(CharacterData character, uint skillId, string name)
            {
                List<CharacterSkill> matches = character.EnhanceSkillList
                    .Where(skill => skill.Id == skillId)
                    .ToList();
                AssertEqual(1, matches.Count, $"{name} enhance skill count");
                return matches[0];
            }
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

        private static void ValidatePrequelRewardCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForStoryDeployVersionGapCompatibility();
            const string requestName = "ReceivePrequelRewardRequest";
            const string responseName = "ReceivePrequelRewardResponse";
            const int firstStageId = 13_012_411;
            const int secondStageId = 13_012_412;
            const long playerId = 88_011;
            const int firstPacketId = 13_020;
            const int secondPacketId = 13_021;

            AscNet.Common.Database.Stage stage = CreateLoginAccountCompatibilityStage(playerId);
            AddPassedPrequelStage(stage, firstStageId);
            AddPassedPrequelStage(stage, secondStageId);
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
            using LoopbackSessionHarness harness = new(
                character,
                player,
                inventory,
                "prequel-reward-compat-test");
            harness.Session.stage = stage;

            JObject firstResponse = ClaimPrequelRewardAndReadResponse(
                harness,
                requestName,
                responseName,
                firstPacketId,
                firstStageId,
                "ReceivePrequelRewardRequest stage 13012411");
            AssertPrequelRewardResponse(
                firstResponse,
                firstStageId,
                [
                    (RewardType: 1, TemplateId: 3, Count: 30, Level: 0, Id: 130_101_110),
                    (RewardType: 1, TemplateId: 1, Count: 1_000, Level: 0, Id: 130_101_111),
                    (RewardType: 3, TemplateId: 2_993_001, Count: 1, Level: 1, Id: 130_101_112)
                ],
                "ReceivePrequelRewardResponse stage 13012411");
            AssertIntegerList([firstStageId], stage.PrequelRewardedStages.Select(stageId => (long)stageId).ToArray(), "ReceivePrequelRewardRequest stage 13012411 persisted rewarded stages");

            JObject secondResponse = ClaimPrequelRewardAndReadResponse(
                harness,
                requestName,
                responseName,
                secondPacketId,
                secondStageId,
                "ReceivePrequelRewardRequest stage 13012412");
            AssertPrequelRewardResponse(
                secondResponse,
                secondStageId,
                [
                    (RewardType: 1, TemplateId: 3, Count: 30, Level: 0, Id: 130_101_120),
                    (RewardType: 1, TemplateId: 1, Count: 2_000, Level: 0, Id: 130_101_121),
                    (RewardType: 3, TemplateId: 3_913_001, Count: 1, Level: 1, Id: 130_101_122)
                ],
                "ReceivePrequelRewardResponse stage 13012412");
            AssertIntegerList([firstStageId, secondStageId], stage.PrequelRewardedStages.Select(stageId => (long)stageId).ToArray(), "ReceivePrequelRewardRequest persisted rewarded stages after second captured claim");

            Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
            MethodInfo doLogin = RequiredMethod(
                accountModule,
                "DoLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            doLogin.Invoke(null, [harness.Session]);
            NotifyFubenPrequelData loginPrequelData = ReadStartupPrequelData(
                harness,
                maxStartupPushes: 192,
                "AccountModule.DoLogin prequel startup payload after captured rewards");
            if (loginPrequelData.FubenPrequelData is null)
                throw new InvalidDataException("NotifyFubenPrequelData login payload: expected FubenPrequelData.");
            AssertIntegerList(
                [firstStageId, secondStageId],
                loginPrequelData.FubenPrequelData.RewardedStages.Select(stageId => (long)stageId).ToArray(),
                "NotifyFubenPrequelData login payload RewardedStages");
            AssertEmptyList(loginPrequelData.FubenPrequelData.UnlockChallengeStages, "NotifyFubenPrequelData login payload UnlockChallengeStages");

            static void AddPassedPrequelStage(AscNet.Common.Database.Stage stage, int stageId)
            {
                stage.AddStage(new StageDatum
                {
                    StageId = stageId,
                    StarsMark = 7,
                    Achievement = 0,
                    Passed = true,
                    PassTimesToday = 0,
                    PassTimesTotal = 1,
                    BuyCount = 0,
                    Score = 0,
                    LastPassTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }

            static JObject ClaimPrequelRewardAndReadResponse(
                LoopbackSessionHarness harness,
                string requestName,
                string responseName,
                int packetId,
                int stageId,
                string name)
            {
                byte[] requestPayload = MessagePackSerializer.Serialize(new Dictionary<string, int>
                {
                    ["StageId"] = stageId
                });
                RequestPacketHandlerDelegate handler = GetRegisteredRequestHandler(requestName);
                Packet.Request packet = new()
                {
                    Id = packetId,
                    Name = requestName,
                    Content = requestPayload
                };

                try
                {
                    handler.Invoke(harness.Session, packet);
                }
                catch (Exception exception)
                {
                    throw new InvalidDataException($"{name}: registered handler invocation failed.", exception);
                }

                AssertNextPushName(harness, nameof(NotifyItemDataList), $"{name} first outbound packet");
                AssertNextPushName(harness, nameof(NotifyEquipDataList), $"{name} second outbound packet");

                Packet responsePacket = harness.ReadPacket($"{name} third outbound packet");
                AssertEqual(Packet.ContentType.Response, responsePacket.Type, $"{name} response packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(responsePacket.Content);
                AssertEqual(packetId, response.Id, $"{name} response packet id");
                AssertEqual(responseName, response.Name, $"{name} response packet name");
                return JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
            }

            static void AssertNextPushName(LoopbackSessionHarness harness, string expectedPushName, string name)
            {
                Packet packet = harness.ReadPacket(name);
                AssertEqual(Packet.ContentType.Push, packet.Type, $"{name} packet type");
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                AssertEqual(expectedPushName, push.Name, $"{name} push name");
            }

            static void AssertPrequelRewardResponse(
                JObject response,
                int expectedStageId,
                IReadOnlyList<(int RewardType, int TemplateId, int Count, int Level, int Id)> expectedRewards,
                string name)
            {
                AssertEqual(0, RequiredValue<int>(response, "Code", JTokenType.Integer, name), $"{name} Code");
                AssertEqual(expectedStageId, RequiredValue<int>(response, "StageId", JTokenType.Integer, name), $"{name} StageId");
                JArray rewards = (JArray)RequiredToken(response, "RewardGoodsList", JTokenType.Array, name);
                AssertEqual(expectedRewards.Count, rewards.Count, $"{name} RewardGoodsList count");

                for (int index = 0; index < expectedRewards.Count; index++)
                {
                    if (rewards[index] is not JObject reward)
                        throw new InvalidDataException($"{name} RewardGoodsList[{index}]: expected JSON object.");
                    (int rewardType, int templateId, int count, int level, int id) = expectedRewards[index];
                    AssertEqual(rewardType, RequiredValue<int>(reward, "RewardType", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].RewardType");
                    AssertEqual(templateId, RequiredValue<int>(reward, "TemplateId", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].TemplateId");
                    AssertEqual(count, RequiredValue<int>(reward, "Count", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].Count");
                    AssertEqual(level, RequiredValue<int>(reward, "Level", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].Level");
                    AssertEqual(0, RequiredValue<int>(reward, "Quality", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].Quality");
                    AssertEqual(0, RequiredValue<int>(reward, "Grade", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].Grade");
                    AssertEqual(0, RequiredValue<int>(reward, "Breakthrough", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].Breakthrough");
                    AssertEqual(0, RequiredValue<int>(reward, "ConvertFrom", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].ConvertFrom");
                    AssertEqual(false, RequiredValue<bool>(reward, "IsGift", JTokenType.Boolean, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].IsGift");
                    AssertEqual(0, RequiredValue<int>(reward, "RewardMulti", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].RewardMulti");
                    AssertEqual(id, RequiredValue<int>(reward, "Id", JTokenType.Integer, $"{name} RewardGoodsList[{index}]"), $"{name} RewardGoodsList[{index}].Id");
                }
            }

            static NotifyFubenPrequelData ReadStartupPrequelData(LoopbackSessionHarness harness, int maxStartupPushes, string name)
            {
                List<string> pushNames = new();
                for (int packetIndex = 0; packetIndex < maxStartupPushes; packetIndex++)
                {
                    Packet packet = harness.ReadPacket($"{name} {packetIndex + 1}");
                    AssertEqual(Packet.ContentType.Push, packet.Type, $"{name} {packetIndex + 1} packet type");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    pushNames.Add(push.Name);
                    if (push.Name == nameof(NotifyFubenPrequelData))
                        return MessagePackSerializer.Deserialize<NotifyFubenPrequelData>(push.Content);
                }

                throw new InvalidDataException($"{name}: expected NotifyFubenPrequelData; observed {(pushNames.Count == 0 ? "<none>" : string.Join(", ", pushNames))}.");
            }
        }

        private static void ValidateStoryDeployVersionGapCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForStoryDeployVersionGapCompatibility();
            const uint retailRebootStageId = 10_400_101;
            const int retailRebootStageRobotId = 172_410;
            const uint retailRebootStageRobotCharacterId = 1_021_007;
            const uint retailMainLine2StageId = 15_020_121;
            const int retailMainLine2StageRobotId = 9_108;
            const long playerId = 88_010;
            const int localCharacterId = 1_021_001;
            const int preFightPacketId = 13_014;
            const int fightSettlePacketId = 13_015;
            const int enterStoryPacketId = 13_016;
            const int mainLine2ChapterPacketId = 13_017;

            StageTable retailRebootStage = AssertRetailStageTableProgressionCompatibility(
                retailRebootStageId,
                expectedRebootId: 17,
                expectedPassTimeLimit: 300,
                expectedRobotId: retailRebootStageRobotId);
            RobotTable requestedRobot = AssertStoryDeployRobotCharacterTableCompatibility(retailRebootStageRobotId, retailRebootStageRobotCharacterId);
            AssertRetailCapturedStageDeployPath(
                (uint)retailRebootStage.StageId,
                retailRebootStageRobotId,
                requestedRobot,
                expectedRebootId: 17,
                expectedPassTimeLimit: 300,
                playerId,
                preFightPacketId,
                fightSettlePacketId);

            AssertRetailStageTableProgressionCompatibility(
                retailMainLine2StageId,
                expectedRebootId: 16,
                expectedPassTimeLimit: 300,
                expectedRobotId: retailMainLine2StageRobotId);
            AssertRetailStageLevelControlMonsterLevels(retailMainLine2StageId, [300, 300, 300]);
            AssertMainLine2GeneratedTableCoverage(retailRebootStageId, [18, 54, 61]);
            AssertMainlineChapterStageReferencesBackedByStageRows([1024, 1025, 1026, 1027, 1028, 1029]);
            AssertRetailFirstClearSettleRewards(
                retailMainLine2StageId,
                playerId + 2,
                localCharacterId,
                preFightPacketId,
                fightSettlePacketId);

            IReadOnlyList<uint> capturedVersionGapStageIds = CapturedMainLine2MissingStageIds();
            AssertCapturedMainLine2VersionGapStagesBackedByLocalStageTable(capturedVersionGapStageIds);
            AssertCapturedMainLine2ChapterUpdates(playerId, [18, 61, 54], mainLine2ChapterPacketId);

            AscNet.Common.Database.Player player = CreateStoryDeployVersionGapPlayer(playerId, localCharacterId);
            AscNet.Common.Database.Character character = CreateStoryDeployVersionGapCharacter(playerId, localCharacterId);
            AscNet.Common.Database.Stage fightStage = CreateLoginAccountCompatibilityStage(playerId);
            using LoopbackSessionHarness fightHarness = new(
                character,
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                "story-deploy-version-gap-mainline2-fight-test");
            fightHarness.Session.stage = fightStage;

            foreach (uint stageId in capturedVersionGapStageIds)
            {
                AssertCapturedMainLine2StageFightPathPlayable(
                    fightHarness,
                    fightStage,
                    playerId,
                    localCharacterId,
                    stageId,
                    preFightPacketId,
                    fightSettlePacketId);
            }

            AscNet.Common.Database.Stage enterStoryStage = CreateLoginAccountCompatibilityStage(playerId + 1);
            using LoopbackSessionHarness enterStoryHarness = new(
                CreateStoryDeployVersionGapCharacter(playerId + 1, localCharacterId),
                CreateStoryDeployVersionGapPlayer(playerId + 1, localCharacterId),
                CreateDrawCompatibilityInventory(playerId + 1, []),
                "story-deploy-version-gap-mainline2-enter-story-test");
            enterStoryHarness.Session.stage = enterStoryStage;

            foreach (uint stageId in capturedVersionGapStageIds)
            {
                AssertCapturedMainLine2StageEnterStoryPlayable(
                    enterStoryHarness,
                    enterStoryStage,
                    stageId,
                    enterStoryPacketId);
            }
        }

        private static IReadOnlyList<uint> CapturedMainLine2MissingStageIds()
        {
            return
            [
                10_310_102, 10_310_105, 10_310_109, 10_310_112, 10_310_116, 10_310_119,
                10_310_122, 10_310_125, 10_310_126, 10_310_127, 10_310_212, 10_310_216,
                10_310_316, 10_320_102, 10_320_107, 10_320_109, 10_320_111, 10_320_116,
                10_320_119, 10_320_122, 10_320_216, 10_320_316, 10_320_416, 10_330_102,
                10_330_105, 10_330_108, 10_330_113, 10_330_116, 10_330_119, 10_330_122,
                10_330_127, 10_330_130, 10_330_213, 10_330_308, 10_340_101, 10_340_105,
                10_340_111, 10_340_116, 10_340_117, 10_350_101, 10_350_111, 10_350_114,
                10_350_115, 10_350_122, 10_350_125, 10_350_130, 10_350_132, 10_350_135,
                10_350_136, 10_350_230, 15_020_899, 15_020_902, 15_021_500,
                15_021_501, 15_021_502
            ];
        }

        private static StageTable AssertRetailStageTableProgressionCompatibility(
            uint stageId,
            int expectedRebootId,
            int expectedPassTimeLimit,
            int expectedRobotId)
        {
            StageTable stage = TableReaderV2.Parse<StageTable>().SingleOrDefault(stage => (uint)stage.StageId == stageId)
                ?? throw new InvalidDataException($"Stage.tsv retail captured stage {stageId}: missing row.");
            AssertEqual(expectedRebootId, stage.RebootId ?? 0, $"Stage.tsv retail captured stage {stageId} RebootId");
            AssertEqual(expectedPassTimeLimit, stage.PassTimeLimit ?? 0, $"Stage.tsv retail captured stage {stageId} PassTimeLimit");
            if (!stage.RobotId.Contains(expectedRobotId))
                throw new InvalidDataException($"Stage.tsv retail captured stage {stageId} RobotId: missing robot {expectedRobotId}.");
            return stage;
        }

        private static void AssertRetailStageLevelControlMonsterLevels(uint stageId, IReadOnlyList<long> expectedMonsterLevels)
        {
            List<StageLevelControlTable> levelControls = TableReaderV2.Parse<StageLevelControlTable>()
                .Where(levelControl => (uint)levelControl.StageId == stageId)
                .ToList();
            if (levelControls.Count == 0)
                throw new InvalidDataException($"StageLevelControl.tsv retail captured stage {stageId}: missing rows.");

            foreach (StageLevelControlTable levelControl in levelControls)
            {
                AssertIntegerList(
                    expectedMonsterLevels,
                    levelControl.MonsterLevel.Select(level => (long)level).ToArray(),
                    $"StageLevelControl.tsv retail captured stage {stageId} row {levelControl.Id} MonsterLevel");
            }
        }

        private static void AssertMainlineChapterStageReferencesBackedByStageRows(IReadOnlyList<int> chapterIds)
        {
            HashSet<int> stageIds = TableReaderV2.Parse<StageTable>()
                .Select(stage => stage.StageId)
                .ToHashSet();
            Dictionary<int, ChapterTable> chaptersById = TableReaderV2.Parse<ChapterTable>()
                .GroupBy(chapter => chapter.ChapterId)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (int chapterId in chapterIds)
            {
                if (!chaptersById.TryGetValue(chapterId, out ChapterTable? chapter))
                    throw new InvalidDataException($"Chapter.tsv row {chapterId}: missing chapter row.");

                List<int> referencedStageIds = chapter.StageId
                    .Where(stageId => stageId > 0)
                    .ToList();
                if (referencedStageIds.Count == 0)
                    throw new InvalidDataException($"Chapter.tsv row {chapterId}: expected at least one stage reference.");

                foreach (int stageId in referencedStageIds)
                {
                    if (!stageIds.Contains(stageId))
                        throw new InvalidDataException($"Chapter.tsv row {chapterId}: StageId {stageId} is missing from Stage.tsv.");
                }
            }
        }

        private static void AssertMainLine2GeneratedTableCoverage(uint expectedStageId, IReadOnlyList<int> expectedChapterIds)
        {
            Type[] mainLine2TableTypes = typeof(StageTable).Assembly.GetTypes()
                .Where(type => type.Namespace == "AscNet.Table.V2.share.fuben.mainline2"
                    && typeof(ITable).IsAssignableFrom(type)
                    && !type.IsAbstract)
                .ToArray();
            if (mainLine2TableTypes.Length == 0)
                return;

            Type[] chapterLikeTypes = mainLine2TableTypes
                .Where(type => type.Name.Contains("Chapter", StringComparison.Ordinal)
                    || OptionalDataMember(type, "ChapterId") is not null)
                .ToArray();
            if (chapterLikeTypes.Length == 0)
                throw new InvalidDataException("MainLine2 generated tables: expected chapter or exhibition chapter table classes.");

            HashSet<int> actualChapterIds = new();
            foreach (Type chapterType in chapterLikeTypes)
            {
                foreach (object row in ParseTableRows(chapterType, $"MainLine2 {chapterType.Name}"))
                {
                    if (TryGetFirstIntegerMember(row, ["ChapterId", "Id"], out int chapterId))
                        actualChapterIds.Add(chapterId);
                }
            }

            foreach (int expectedChapterId in expectedChapterIds)
            {
                if (!actualChapterIds.Contains(expectedChapterId))
                    throw new InvalidDataException($"MainLine2 chapter/exhibition generated tables: missing id {expectedChapterId}.");
            }

            Type[] stageLikeTypes = mainLine2TableTypes
                .Where(type => type.Name.Contains("Stage", StringComparison.Ordinal)
                    && (OptionalDataMember(type, "StageId") is not null
                        || OptionalDataMember(type, "Id") is not null
                        || OptionalDataMember(type, "StageIds") is not null))
                .OrderByDescending(type => string.Equals(type.Name, "MainLine2StageTable", StringComparison.Ordinal))
                .ThenBy(type => type.Name, StringComparer.Ordinal)
                .ToArray();
            if (stageLikeTypes.Length == 0)
                throw new InvalidDataException("MainLine2 generated tables: expected a stage table class.");

            bool hasExpectedStage = stageLikeTypes
                .SelectMany(type => ParseTableRows(type, $"MainLine2 {type.Name}"))
                .Any(row => MainLine2RowContainsStageId(row, expectedStageId));
            if (!hasExpectedStage)
                throw new InvalidDataException($"MainLine2 generated stage tables: missing stage {expectedStageId}.");
        }

        private static bool MainLine2RowContainsStageId(object row, uint expectedStageId)
        {
            if (TryGetFirstIntegerMember(row, ["StageId"], out int stageId)
                && (uint)stageId == expectedStageId)
                return true;

            if (string.Equals(row.GetType().Name, "MainLine2StageTable", StringComparison.Ordinal)
                && TryGetFirstIntegerMember(row, ["Id"], out int id)
                && (uint)id == expectedStageId)
                return true;

            MemberInfo? stageIdsMember = OptionalDataMember(row.GetType(), "StageIds");
            if (stageIdsMember is null)
                return false;

            object? stageIds = GetRequiredMemberValue(row, stageIdsMember);
            return stageIds is not null
                && ReadIntegerList(stageIds, $"{row.GetType().FullName}.StageIds")
                    .Any(value => (uint)value == expectedStageId);
        }

        private static List<object> ParseTableRows(Type tableType, string name)
        {
            object? rows = RequiredGenericMethodDefinition(
                    typeof(TableReaderV2),
                    nameof(TableReaderV2.Parse),
                    BindingFlags.Public | BindingFlags.Static,
                    parameterCount: 0)
                .MakeGenericMethod(tableType)
                .Invoke(null, null);
            if (rows is not System.Collections.IEnumerable enumerable || rows is string)
                throw new InvalidDataException($"{name}: expected TableReaderV2.Parse rows.");

            List<object> result = new();
            foreach (object? row in enumerable)
            {
                if (row is null)
                    throw new InvalidDataException($"{name}: expected non-nil table row.");
                result.Add(row);
            }
            return result;
        }

        private static bool TryGetFirstIntegerMember(object target, IReadOnlyList<string> memberNames, out int value)
        {
            foreach (string memberName in memberNames)
            {
                MemberInfo? member = OptionalDataMember(target.GetType(), memberName);
                if (member is null)
                    continue;

                object? rawValue = GetRequiredMemberValue(target, member);
                if (rawValue is null)
                    continue;

                value = Convert.ToInt32(rawValue);
                return true;
            }

            value = 0;
            return false;
        }

        private static void AssertRetailFirstClearSettleRewards(
            uint stageId,
            long playerId,
            int localCharacterId,
            int preFightPacketId,
            int fightSettlePacketId)
        {
            AscNet.Common.Database.Stage stage = CreateLoginAccountCompatibilityStage(playerId);
            using LoopbackSessionHarness harness = new(
                CreateStoryDeployVersionGapCharacter(playerId, localCharacterId),
                CreateStoryDeployVersionGapPlayer(playerId, localCharacterId),
                CreateDrawCompatibilityInventory(playerId, []),
                "story-deploy-version-gap-mainline2-first-clear-settle-test");
            harness.Session.stage = stage;

            PreFightRequest preFightRequest = new()
            {
                PreFightData = new()
                {
                    ChallengeCount = 1,
                    StageId = stageId,
                    CardIds = [unchecked((uint)localCharacterId)],
                    RobotIds = [],
                    FirstFightPos = 1,
                    CaptainPos = 1,
                    IsHasAssist = false
                }
            };

            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, preFightPacketId, preFightRequest);
            PreFightResponse preFightResponse = ReadResponsePayload<PreFightResponse>(
                harness,
                preFightPacketId,
                nameof(PreFightResponse),
                $"PreFightRequest retail captured stage {stageId} first-clear settle response");
            AssertEqual(0, preFightResponse.Code, $"PreFightResponse retail captured stage {stageId} first-clear settle Code");
            if (preFightResponse.FightData is null)
                throw new InvalidDataException($"PreFightResponse retail captured stage {stageId} first-clear settle: expected FightData.");

            InvokeRegisteredRequestHandler(
                nameof(FightSettleRequest),
                harness.Session,
                fightSettlePacketId,
                CreateMissingStageSettleRequest(stageId, preFightResponse.FightData.FightId, playerId));
            (NotifyStageData stagePush, FightSettleResponse settleResponse) = ReadUnknownStageSettleResult(
                harness,
                fightSettlePacketId);
            AssertRetailFirstClearSettleResponse(settleResponse, stageId);
            AssertMissingStagePushAndPersistence(stagePush, stage, stageId, $"retail captured stage {stageId} first-clear settle");
        }

        private static void AssertRetailFirstClearSettleResponse(FightSettleResponse settleResponse, uint stageId)
        {
            AssertEqual(0, settleResponse.Code, $"FightSettleResponse retail captured stage {stageId} first-clear Code");
            if (settleResponse.Settle is null)
                throw new InvalidDataException($"FightSettleResponse retail captured stage {stageId} first-clear: expected Settle payload.");
            AssertEqual(stageId, (uint)settleResponse.Settle.StageId, $"FightSettleResponse retail captured stage {stageId} first-clear Settle.StageId");
            AssertEqual(1, settleResponse.Settle.ChallengeCount, $"FightSettleResponse retail captured stage {stageId} first-clear ChallengeCount");
            AssertRetailFirstClearRewardGoods(settleResponse.Settle.RewardGoodsList, $"FightSettleResponse retail captured stage {stageId} first-clear RewardGoodsList");
            AssertEqual(1, settleResponse.Settle.MultiRewardGoodsList.Count, $"FightSettleResponse retail captured stage {stageId} first-clear MultiRewardGoodsList count");
            AssertRetailFirstClearRewardGoods(settleResponse.Settle.MultiRewardGoodsList[0], $"FightSettleResponse retail captured stage {stageId} first-clear MultiRewardGoodsList[0]");
        }

        private static void AssertRetailFirstClearRewardGoods(IReadOnlyList<RewardGoods> actualRewards, string name)
        {
            (int TemplateId, int Count)[] expectedRewards =
            [
                (3, 30),
                (31_204, 1),
                (30_013, 1),
                (1, 10_000)
            ];

            AssertEqual(expectedRewards.Length, actualRewards.Count, $"{name} count");
            for (int index = 0; index < expectedRewards.Length; index++)
            {
                AssertEqual(expectedRewards[index].TemplateId, actualRewards[index].TemplateId, $"{name}[{index}].TemplateId");
                AssertEqual(expectedRewards[index].Count, actualRewards[index].Count, $"{name}[{index}].Count");
            }
        }

        private static void AssertCapturedMainLine2VersionGapStagesBackedByLocalStageTable(IReadOnlyList<uint> capturedStageIds)
        {
            AssertEqual(55, capturedStageIds.Count, "retail MainLine2 capture version-gap Stage.tsv stage id count");
            AssertEqual(capturedStageIds.Count, capturedStageIds.Distinct().Count(), "retail MainLine2 capture version-gap Stage.tsv distinct stage id count");

            HashSet<uint> localStageIds = TableReaderV2.Parse<StageTable>()
                .Select(stage => (uint)stage.StageId)
                .ToHashSet();
            foreach (uint stageId in capturedStageIds)
            {
                AssertEqual(true, localStageIds.Contains(stageId), $"retail MainLine2 capture stage {stageId} backed by local Stage.tsv");
            }
        }

        private static void AssertCapturedMainLine2ChapterUpdates(long playerId, IReadOnlyList<int> chapterIds, int packetId)
        {
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                CreateDrawCompatibilityPlayer(playerId),
                CreateDrawCompatibilityInventory(playerId, []),
                "story-deploy-version-gap-mainline2-chapter-test");

            foreach (int chapterId in chapterIds)
            {
                InvokeRegisteredRequestHandler(
                    nameof(MainLine2UpdateExhibitionChapterRequest),
                    harness.Session,
                    packetId,
                    new MainLine2UpdateExhibitionChapterRequest { ChapterId = chapterId });
                MainLine2UpdateExhibitionChapterResponse response = ReadResponsePayload<MainLine2UpdateExhibitionChapterResponse>(
                    harness,
                    packetId,
                    nameof(MainLine2UpdateExhibitionChapterResponse),
                    $"MainLine2UpdateExhibitionChapterRequest captured chapter {chapterId} response");
                AssertEqual(0, response.Code, $"MainLine2UpdateExhibitionChapterResponse captured chapter {chapterId} Code");
            }
        }

        private static AscNet.Common.Database.Player CreateStoryDeployVersionGapPlayer(long playerId, int localCharacterId)
        {
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.PlayerData.CurrTeamId = 1;
            player.TeamGroups[1] = new TeamGroupDatum
            {
                TeamType = 1,
                TeamId = 1,
                CaptainPos = 1,
                FirstFightPos = 1,
                TeamData = new()
                {
                    { 1, localCharacterId },
                    { 2, 0 },
                    { 3, 0 }
                },
                TeamName = "VersionGap"
            };
            return player;
        }

        private static AscNet.Common.Database.Character CreateStoryDeployVersionGapCharacter(long playerId, int localCharacterId)
        {
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.AddCharacter((uint)localCharacterId, 80);
            return character;
        }

        private static void AssertRetailCapturedStageDeployPath(
            uint retailStageId,
            int retailRobotId,
            RobotTable requestedRobot,
            int expectedRebootId,
            int expectedPassTimeLimit,
            long playerId,
            int preFightPacketId,
            int fightSettlePacketId)
        {
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            AscNet.Common.Database.Stage stage = CreateLoginAccountCompatibilityStage(playerId);
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                "story-deploy-version-gap-retail-stage-compat-test");
            harness.Session.stage = stage;

            PreFightRequest preFightRequest = new()
            {
                PreFightData = new()
                {
                    ChallengeCount = 1,
                    StageId = retailStageId,
                    CardIds = null,
                    RobotIds = null,
                    FirstFightPos = 1,
                    CaptainPos = 1,
                    IsHasAssist = false
                }
            };

            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, preFightPacketId, preFightRequest);
            PreFightResponse preFightResponse = ReadResponsePayload<PreFightResponse>(
                harness,
                preFightPacketId,
                nameof(PreFightResponse),
                $"PreFightRequest retail captured stage {retailStageId} response");
            AssertEqual(0, preFightResponse.Code, $"PreFightResponse retail captured stage {retailStageId} Code");
            if (preFightResponse.FightData is null)
                throw new InvalidDataException($"PreFightResponse retail captured stage {retailStageId}: expected FightData for accepted deploy.");
            AssertEqual(retailStageId, preFightResponse.FightData.StageId, $"PreFightResponse retail captured stage {retailStageId} FightData.StageId");
            AssertEqual(expectedRebootId, preFightResponse.FightData.RebootId, $"PreFightResponse retail captured stage {retailStageId} FightData.RebootId");
            AssertEqual(expectedPassTimeLimit, preFightResponse.FightData.PassTimeLimit, $"PreFightResponse retail captured stage {retailStageId} FightData.PassTimeLimit");
            AssertPreFightDeployedCharacterIds(
                preFightResponse,
                playerId,
                [(long)requestedRobot.CharacterId],
                $"PreFightResponse retail captured stage {retailStageId} Stage.tsv robot {retailRobotId} deployed Robot.tsv CharacterId");

            InvokeRegisteredRequestHandler(
                nameof(FightSettleRequest),
                harness.Session,
                fightSettlePacketId,
                CreateMissingStageSettleRequest(retailStageId, preFightResponse.FightData.FightId, playerId));
            (NotifyStageData stagePush, FightSettleResponse settleResponse) = ReadUnknownStageSettleResult(
                harness,
                fightSettlePacketId);
            AssertEqual(0, settleResponse.Code, $"FightSettleResponse retail captured stage {retailStageId} Code");
            if (settleResponse.Settle is null)
                throw new InvalidDataException($"FightSettleResponse retail captured stage {retailStageId}: expected Settle payload.");
            AssertEqual(retailStageId, (uint)settleResponse.Settle.StageId, $"FightSettleResponse retail captured stage {retailStageId} Settle.StageId");
            AssertMissingStagePushAndPersistence(stagePush, stage, retailStageId, $"retail captured stage {retailStageId}");
        }

        private static void AssertCapturedMainLine2StageFightPathPlayable(
            LoopbackSessionHarness harness,
            AscNet.Common.Database.Stage stage,
            long playerId,
            int localCharacterId,
            uint stageId,
            int preFightPacketId,
            int fightSettlePacketId)
        {
            string name = $"retail MainLine2 capture missing Stage.tsv stage {stageId}";
            PreFightRequest preFightRequest = new()
            {
                PreFightData = new()
                {
                    ChallengeCount = 1,
                    StageId = stageId,
                    CardIds = [],
                    RobotIds = [],
                    FirstFightPos = 1,
                    CaptainPos = 1,
                    IsHasAssist = false
                }
            };

            InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, preFightPacketId, preFightRequest);
            PreFightResponse preFightResponse = ReadResponsePayload<PreFightResponse>(
                harness,
                preFightPacketId,
                nameof(PreFightResponse),
                $"{name} PreFightResponse");
            AssertEqual(0, preFightResponse.Code, $"{name} PreFightResponse Code");
            if (preFightResponse.FightData is null)
                throw new InvalidDataException($"{name}: expected PreFightResponse FightData for accepted deploy.");
            AssertEqual(stageId, preFightResponse.FightData.StageId, $"{name} PreFightResponse FightData.StageId");
            List<long> expectedCharacterIds = ExpectedStageDeployCharacterIds(stageId, localCharacterId);
            AssertPreFightDeployedCharacterIds(
                preFightResponse,
                playerId,
                expectedCharacterIds,
                $"{name} PreFightResponse deployed character ids");

            InvokeRegisteredRequestHandler(
                nameof(FightSettleRequest),
                harness.Session,
                fightSettlePacketId,
                CreateMissingStageSettleRequest(stageId, preFightResponse.FightData.FightId, playerId));
            (NotifyStageData stagePush, FightSettleResponse settleResponse) = ReadUnknownStageSettleResult(
                harness,
                fightSettlePacketId);
            AssertAcceptedMissingStageSettle(settleResponse, stageId, $"{name} FightSettleResponse");
            AssertMissingStagePushAndPersistence(stagePush, stage, stageId, name);
        }

        private static FightSettleRequest CreateMissingStageSettleRequest(uint stageId, long fightId, long playerId)
        {
            return new FightSettleRequest
            {
                Result = new()
                {
                    IsWin = true,
                    IsForceExit = false,
                    StageId = stageId,
                    FightId = fightId,
                    AddStars = 7,
                    PlayerIds = [playerId],
                    PlayerData = [],
                    Operations = new(),
                    Codes = [],
                    NpcHpInfo = new(),
                    NpcDpsTable = new(),
                    EventSet = [],
                    GroupDropDatas = []
                }
            };
        }

        private static void AssertAcceptedMissingStageSettle(FightSettleResponse settleResponse, uint stageId, string name)
        {
            AssertEqual(0, settleResponse.Code, $"{name} Code");
            if (settleResponse.Settle is null)
                throw new InvalidDataException($"{name}: expected Settle payload for accepted settle.");
            AssertEqual(stageId, (uint)settleResponse.Settle.StageId, $"{name} Settle.StageId");
        }

        private static List<long> ExpectedStageDeployCharacterIds(uint stageId, int fallbackCharacterId)
        {
            StageTable stage = TableReaderV2.Parse<StageTable>().Single(stage => (uint)stage.StageId == stageId);
            Dictionary<int, int> robotCharacterIds = TableReaderV2.Parse<RobotTable>()
                .Where(robot => Convert.ToInt32(robot.CharacterId) > 0)
                .ToDictionary(robot => robot.Id, robot => Convert.ToInt32(robot.CharacterId));
            List<long> expectedRobotCharacterIds = stage.RobotId
                .Where(robotId => robotId > 0 && robotCharacterIds.ContainsKey(robotId))
                .Select(robotId => (long)robotCharacterIds[robotId])
                .ToList();
            return expectedRobotCharacterIds.Count > 0
                ? expectedRobotCharacterIds
                : [fallbackCharacterId];
        }

        private static void AssertPreFightDeployedCharacterIds(
            PreFightResponse preFightResponse,
            long playerId,
            IReadOnlyList<long> expectedCharacterIds,
            string name)
        {
            if (preFightResponse.FightData is null)
                throw new InvalidDataException($"{name}: expected FightData.");
            PreFightResponse.PreFightResponseFightData.PreFightResponseFightDataRoleData playerRole = preFightResponse.FightData.RoleData.SingleOrDefault(role => role.Id == (uint)playerId)
                ?? throw new InvalidDataException($"{name}: expected player RoleData for accepted deploy.");
            if (playerRole.NpcData is null)
                throw new InvalidDataException($"{name}: expected NpcData for accepted deploy.");
            AssertIntegerList(
                expectedCharacterIds,
                playerRole.NpcData
                    .OrderBy(npc => npc.Key)
                    .Select(npc => (long)RequiredNpcCharacterId(npc, name))
                    .ToArray(),
                name);
        }

        private static void AssertMissingStagePushAndPersistence(
            NotifyStageData stagePush,
            AscNet.Common.Database.Stage stage,
            uint stageId,
            string name)
        {
            StageDatum pushedStage = stagePush.StageList.SingleOrDefault(stageDatum => stageDatum.StageId == stageId)
                ?? throw new InvalidDataException($"NotifyStageData {name}: missing passed stage {stageId}.");
            AssertPassedUnknownStageDatum(pushedStage, $"NotifyStageData {name}");

            if (!stage.Stages.TryGetValue(stageId, out StageDatum? persistedStage))
                throw new InvalidDataException($"{name}: session stage data did not persist passed stage {stageId}.");
            AssertPassedUnknownStageDatum(persistedStage, $"Persisted {name}");
        }

        private static void AssertCapturedMainLine2StageEnterStoryPlayable(
            LoopbackSessionHarness harness,
            AscNet.Common.Database.Stage stage,
            uint stageId,
            int packetId)
        {
            string name = $"retail MainLine2 capture missing Stage.tsv stage {stageId} EnterStory";
            if (stage.Stages.ContainsKey(stageId))
                throw new InvalidDataException($"{name}: test setup expected EnterStory to be the first writer for this stage.");

            InvokeRegisteredRequestHandler(
                nameof(EnterStoryRequest),
                harness.Session,
                packetId,
                new EnterStoryRequest { StageId = (int)stageId });
            (NotifyStageData stagePush, EnterStoryResponse response) = ReadUnknownStageEnterStoryResult(harness, packetId, name);
            AssertEqual(0, response.Code, $"{name} Code");
            AssertMissingStagePushAndPersistence(stagePush, stage, stageId, name);
        }

        private static (NotifyStageData StagePush, EnterStoryResponse Response) ReadUnknownStageEnterStoryResult(
            LoopbackSessionHarness harness,
            int expectedPacketId,
            string name)
        {
            NotifyStageData? stagePush = null;
            EnterStoryResponse? enterStoryResponse = null;

            for (int packetIndex = 0; packetIndex < 3 && (stagePush is null || enterStoryResponse is null); packetIndex++)
            {
                Packet packet = packetIndex == 0
                    ? harness.ReadPacket($"{name} first packet")
                    : harness.ReadPacket($"{name} packet {packetIndex + 1}");

                switch (packet.Type)
                {
                    case Packet.ContentType.Push:
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        if (push.Name == nameof(NotifyStageData))
                        {
                            stagePush = MessagePackSerializer.Deserialize<NotifyStageData>(push.Content);
                        }
                        break;
                    case Packet.ContentType.Response:
                        Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                        AssertEqual(expectedPacketId, response.Id, $"{name} packet id");
                        AssertEqual(nameof(EnterStoryResponse), response.Name, $"{name} packet name");
                        enterStoryResponse = MessagePackSerializer.Deserialize<EnterStoryResponse>(response.Content);
                        break;
                    default:
                        throw new InvalidDataException($"{name}: unexpected packet type {packet.Type}.");
                }
            }

            if (stagePush is null)
                throw new InvalidDataException($"{name}: expected NotifyStageData push.");
            if (enterStoryResponse is null)
                throw new InvalidDataException($"{name}: expected EnterStoryResponse.");

            return (stagePush, enterStoryResponse);
        }

        private static RobotTable AssertStoryDeployRobotCharacterTableCompatibility(int retailRobotId, uint retailRobotCharacterId)
        {
            Dictionary<int, CharacterTable> characterRowsById = TableReaderV2.Parse<CharacterTable>()
                .ToDictionary(character => character.Id);
            List<RobotTable> robotRows = TableReaderV2.Parse<RobotTable>();

            foreach (RobotTable robot in robotRows)
            {
                int characterId = Convert.ToInt32(robot.CharacterId);
                if (!characterRowsById.ContainsKey(characterId))
                    throw new InvalidDataException($"Robot.tsv row {robot.Id}: CharacterId {characterId} does not map to an existing Character.tsv row.");
            }

            RobotTable requestedRobot = robotRows.SingleOrDefault(robot => robot.Id == retailRobotId)
                ?? throw new InvalidDataException($"Story deploy version-gap test setup error: Robot.tsv is missing retail captured robot {retailRobotId}.");
            AssertEqual((int)retailRobotCharacterId, Convert.ToInt32(requestedRobot.CharacterId), $"Robot.tsv retail captured robot {retailRobotId} CharacterId");
            return requestedRobot;
        }
        private static uint RequiredNpcCharacterId(KeyValuePair<int, dynamic> npc, string name)
        {
            System.Collections.IDictionary npcData = RequiredDynamicMap(npc.Value, $"{name} NpcData[{npc.Key}]");
            System.Collections.IDictionary character = RequiredDynamicMap(
                RequiredDynamicValue(npcData, "Character", $"{name} NpcData[{npc.Key}]"),
                $"{name} NpcData[{npc.Key}].Character");
            return (uint)RequiredDynamicInteger(character, "Id", $"{name} NpcData[{npc.Key}].Character");
        }


        private static (NotifyStageData StagePush, FightSettleResponse Response) ReadUnknownStageSettleResult(
            LoopbackSessionHarness harness,
            int expectedPacketId)
        {
            NotifyStageData? stagePush = null;
            FightSettleResponse? settleResponse = null;

            for (int packetIndex = 0; packetIndex < 16 && (stagePush is null || settleResponse is null); packetIndex++)
            {
                Packet packet = harness.ReadPacket(packetIndex == 0
                    ? "FightSettleRequest unknown Stage.tsv row first packet"
                    : $"FightSettleRequest unknown Stage.tsv row packet {packetIndex + 1}");

                switch (packet.Type)
                {
                    case Packet.ContentType.Push:
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        if (push.Name == nameof(NotifyStageData))
                        {
                            stagePush = MessagePackSerializer.Deserialize<NotifyStageData>(push.Content);
                        }
                        break;
                    case Packet.ContentType.Response:
                        Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                        AssertEqual(expectedPacketId, response.Id, "FightSettleResponse unknown Stage.tsv row packet id");
                        AssertEqual(nameof(FightSettleResponse), response.Name, "FightSettleResponse unknown Stage.tsv row packet name");
                        settleResponse = MessagePackSerializer.Deserialize<FightSettleResponse>(response.Content);
                        break;
                    default:
                        throw new InvalidDataException($"FightSettleRequest unknown Stage.tsv row: unexpected packet type {packet.Type}.");
                }
            }

            if (stagePush is null)
                throw new InvalidDataException("FightSettleRequest unknown Stage.tsv row: expected NotifyStageData push.");
            if (settleResponse is null)
                throw new InvalidDataException("FightSettleRequest unknown Stage.tsv row: expected FightSettleResponse.");

            return (stagePush, settleResponse);
        }

        private static void AssertPassedUnknownStageDatum(StageDatum stageDatum, string name)
        {
            AssertEqual(true, stageDatum.Passed, $"{name} Passed");
            AssertEqual(7L, stageDatum.StarsMark, $"{name} StarsMark");
            AssertEqual(1L, stageDatum.PassTimesTotal, $"{name} PassTimesTotal");
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
                    ActivityNo = 260,
                    TotalScore = 0,
                    MaxScore = 0,
                    OldLevelType = 8,
                    LevelType = 8,
                    ChallengeCount = 0,
                    RemainTime = 3600 * 24,
                    AutoFightCount = 0,
                    RankPlatform = 1,
                    TrialStageInfoList =
                    [
                        BuildBossSingleStageInfo(30302803),
                        BuildBossSingleStageInfo(30302804),
                        BuildBossSingleStageInfo(30302805)
                    ],
                    AfreshId = 1,
                    ChallengeLevelType = 0,
                    IsResetOpen = true,
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

            NotifyFubenBossSingleData roundTrip = MessagePackSerializer.Deserialize<NotifyFubenBossSingleData>(
                MessagePackSerializer.Serialize(notification));

            NotifyFubenBossSingleData.NotifyFubenBossSingleDataFubenBossSingleData bossSingleData = roundTrip.FubenBossSingleData
                ?? throw new InvalidDataException("NotifyFubenBossSingleData FubenBossSingleData serialized as nil.");
            AssertEmptyList(bossSingleData.CharacterPoints, "NotifyFubenBossSingleData FubenBossSingleData.CharacterPoints");
            AssertEmptyList(bossSingleData.HistoryList, "NotifyFubenBossSingleData FubenBossSingleData.HistoryList");
            AssertEmptyList(bossSingleData.RewardIds, "NotifyFubenBossSingleData FubenBossSingleData.RewardIds");
            AssertEmptyList(bossSingleData.BossList, "NotifyFubenBossSingleData FubenBossSingleData.BossList");
            AssertEmptyList(bossSingleData.BestiraryStageInfoList, "NotifyFubenBossSingleData FubenBossSingleData.BestiraryStageInfoList");
            AssertEmptyList(bossSingleData.ChallengeStageHistoryList, "NotifyFubenBossSingleData FubenBossSingleData.ChallengeStageHistoryList");
            AssertEmptyList(bossSingleData.StageRecordList, "NotifyFubenBossSingleData FubenBossSingleData.StageRecordList");
            AssertEqual(3, bossSingleData.TrialStageInfoList.Count, "NotifyFubenBossSingleData FubenBossSingleData.TrialStageInfoList count");
            AssertEqual(3, bossSingleData.NormalStageTeamInfos.Count, "NotifyFubenBossSingleData FubenBossSingleData.NormalStageTeamInfos count");
            AssertEqual(true, bossSingleData.IsResetOpen, "NotifyFubenBossSingleData FubenBossSingleData.IsResetOpen");
            AssertEqual(1, bossSingleData.AfreshId, "NotifyFubenBossSingleData FubenBossSingleData.AfreshId");
            AssertEqual(8, bossSingleData.LevelType, "NotifyFubenBossSingleData FubenBossSingleData.LevelType");
            AssertEqual(8, bossSingleData.OldLevelType, "NotifyFubenBossSingleData FubenBossSingleData.OldLevelType");
            AssertEqual(2, roundTrip.BossListDict.Count, "NotifyFubenBossSingleData BossListDict section count");
            AssertBossListDictValues(roundTrip.BossListDict, 7, [102, 104, 109]);
            AssertBossListDictValues(roundTrip.BossListDict, 8, [2030, 2034, 2038]);
            if (!roundTrip.BossListDict.ContainsKey(bossSingleData.LevelType))
                throw new InvalidDataException("NotifyFubenBossSingleData BossListDict: expected a section list for FubenBossSingleData.LevelType.");
            if (bossSingleData.RemainTime == 0)
                throw new InvalidDataException("NotifyFubenBossSingleData FubenBossSingleData.RemainTime: expected a positive value.");

            static Dictionary<string, object> BuildBossSingleStageInfo(int stageId)
            {
                return new()
                {
                    ["StageId"] = stageId,
                    ["Score"] = 0
                };
            }

            static Dictionary<string, object> BuildBossSingleTeamInfo(int sectionId)
            {
                return new()
                {
                    ["SectionId"] = sectionId,
                    ["CharacterIds"] = Array.Empty<int>()
                };
            }

            static void AssertBossListDictValues(
                IReadOnlyDictionary<int, List<int>> bossListDict,
                int sectionId,
                int[] expectedBossIds)
            {
                if (!bossListDict.TryGetValue(sectionId, out List<int>? actualBossIds))
                    throw new InvalidDataException($"NotifyFubenBossSingleData BossListDict: expected section {sectionId}.");
                if (!actualBossIds.SequenceEqual(expectedBossIds))
                    throw new InvalidDataException($"NotifyFubenBossSingleData BossListDict section {sectionId}: expected {string.Join(",", expectedBossIds)}, got {string.Join(",", actualBossIds)}.");
            }
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

        private static object ReadResponsePayload(
            LoopbackSessionHarness harness,
            int expectedPacketId,
            string expectedResponseName,
            string name,
            Type responseType,
            int maxPacketsToRead = 1)
        {
            for (int packetIndex = 0; packetIndex < maxPacketsToRead; packetIndex++)
            {
                Packet packet = harness.ReadPacket(packetIndex == 0 ? name : $"{name} packet {packetIndex + 1}");
                if (packet.Type == Packet.ContentType.Push && maxPacketsToRead > 1)
                    continue;

                AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(expectedPacketId, response.Id, $"{name} packet id");
                AssertEqual(expectedResponseName, response.Name, $"{name} packet name");
                return MessagePackDeserialize(responseType, response.Content)
                    ?? throw new InvalidDataException($"{name}: {responseType.FullName} deserialized as nil.");
            }

            throw new InvalidDataException($"{name}: expected {expectedResponseName} response within {maxPacketsToRead} packets.");
        }


        private static TPush ReadPushPayload<TPush>(LoopbackSessionHarness harness, string expectedPushName, string name)
        {
            Packet packet = harness.ReadPacket(name);
            AssertEqual(Packet.ContentType.Push, packet.Type, $"{name} packet type");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
            AssertEqual(expectedPushName, push.Name, $"{name} packet name");
            return MessagePackSerializer.Deserialize<TPush>(push.Content);
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

            public static byte[] SerializeClientRequestFrame(string requestName, int packetId, object? request)
            {
                byte[] content = request is null ? [] : MessagePackSerialize(request.GetType(), request);
                return SerializeClientRequestFrame(requestName, packetId, content);
            }

            public static byte[] SerializeClientRequestFrameWithTotalLength(string requestName, int packetId, int totalFrameLength)
            {
                if (totalFrameLength <= sizeof(int))
                    throw new ArgumentOutOfRangeException(nameof(totalFrameLength), totalFrameLength, "Client request frame length must include a four-byte packet length prefix and an encrypted packet payload.");

                int low = 0;
                int high = Math.Max(1, totalFrameLength);
                byte[] highFrame = SerializeClientRequestFrame(requestName, packetId, CreateDeterministicPayload(high));
                while (highFrame.Length < totalFrameLength)
                {
                    if (high > totalFrameLength * 4)
                        throw new InvalidDataException($"Could not synthesize a {totalFrameLength}-byte {requestName} client frame; reached {highFrame.Length} bytes with {high} content bytes.");

                    low = high + 1;
                    high *= 2;
                    highFrame = SerializeClientRequestFrame(requestName, packetId, CreateDeterministicPayload(high));
                }

                while (low <= high)
                {
                    int mid = low + ((high - low) / 2);
                    byte[] frame = SerializeClientRequestFrame(requestName, packetId, CreateDeterministicPayload(mid));
                    if (frame.Length == totalFrameLength)
                        return frame;

                    if (frame.Length < totalFrameLength)
                        low = mid + 1;
                    else
                        high = mid - 1;
                }

                int searchStart = Math.Max(0, low - 4096);
                int searchEnd = low + 4096;
                for (int contentLength = searchStart; contentLength <= searchEnd; contentLength++)
                {
                    byte[] frame = SerializeClientRequestFrame(requestName, packetId, CreateDeterministicPayload(contentLength));
                    if (frame.Length == totalFrameLength)
                        return frame;
                }

                throw new InvalidDataException($"Could not synthesize a {totalFrameLength}-byte {requestName} client frame without brittle literal payload data.");
            }

            public void WriteClientBytes(ReadOnlySpan<byte> bytes)
            {
                clientSide.GetStream().Write(bytes);
            }

            private static byte[] SerializeClientRequestFrame(string requestName, int packetId, byte[] content)
            {
                Packet.Request request = new()
                {
                    Id = packetId,
                    Name = requestName,
                    Content = content
                };
                byte[] serializedPacket = MessagePackSerializer.Serialize(new Packet
                {
                    No = 0,
                    Type = Packet.ContentType.Request,
                    Content = MessagePackSerializer.Serialize(request)
                }, PacketSerializerOptions);
                Crypto.HaruCrypt.Encrypt(serializedPacket);

                byte[] frame = GC.AllocateUninitializedArray<byte>(serializedPacket.Length + sizeof(int));
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, sizeof(int)), serializedPacket.Length);
                serializedPacket.AsSpan().CopyTo(frame.AsSpan(sizeof(int)));
                return frame;
            }

            private static byte[] CreateDeterministicPayload(int length)
            {
                byte[] payload = GC.AllocateUninitializedArray<byte>(length);
                uint state = 0x9E3779B9;
                for (int index = 0; index < payload.Length; index++)
                {
                    state = unchecked((state * 1664525) + 1013904223);
                    payload[index] = (byte)(state >> 24);
                }

                return payload;
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

            public bool TryReadAvailablePacket(string name, out Packet packet)
            {
                NetworkStream stream = clientSide.GetStream();
                if (!stream.DataAvailable)
                {
                    packet = default!;
                    return false;
                }

                packet = ReadPacket(name);
                return true;
            }

            public void SendClientPush(string name, byte[] content)
            {
                Packet.Push push = new()
                {
                    Name = name,
                    Content = content
                };
                byte[] serializedPacket = MessagePackSerializer.Serialize(new Packet
                {
                    No = 0,
                    Type = Packet.ContentType.Push,
                    Content = MessagePackSerializer.Serialize(push)
                }, PacketSerializerOptions);
                Crypto.HaruCrypt.Encrypt(serializedPacket);

                byte[] sendBytes = GC.AllocateUninitializedArray<byte>(serializedPacket.Length + sizeof(int));
                BinaryPrimitives.WriteInt32LittleEndian(sendBytes.AsSpan()[..sizeof(int)], serializedPacket.Length);
                Array.Copy(serializedPacket, 0, sendBytes, sizeof(int), serializedPacket.Length);
                clientSide.GetStream().Write(sendBytes);
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

        private static bool GetRequiredBooleanMember(object target, string memberName)
        {
            object? value = GetRequiredMemberValue(target, memberName);
            if (value is null)
                throw new InvalidDataException($"{target.GetType().FullName}.{memberName}: expected a boolean, got nil.");
            if (value is bool boolean)
                return boolean;
            throw new InvalidDataException($"{target.GetType().FullName}.{memberName}: expected a boolean, got {value.GetType().FullName}.");
        }

        private static void SetFirstAvailableIntegerList(object target, IReadOnlyList<string> memberNames, IReadOnlyList<int> values, string name)
        {
            foreach (string memberName in memberNames)
            {
                MemberInfo? member = OptionalDataMember(target.GetType(), memberName);
                if (member is null)
                    continue;

                object list = CreateIntegerList(MemberValueType(member), values, $"{target.GetType().FullName}.{memberName}");
                SetRequiredMemberValue(target, member, list);
                return;
            }

            throw new MissingMemberException(target.GetType().FullName, string.Join("|", memberNames));
        }

        private static List<object> GetRequiredObjectListMember(object target, IReadOnlyList<string> memberNames, string name)
        {
            object? value = GetFirstAvailableMemberValue(target, memberNames);
            return ReadObjectList(value, name);
        }

        private static object? GetFirstAvailableMemberValue(object target, IReadOnlyList<string> memberNames)
        {
            foreach (string memberName in memberNames)
            {
                MemberInfo? member = OptionalDataMember(target.GetType(), memberName);
                if (member is not null)
                    return GetRequiredMemberValue(target, member);
            }

            throw new MissingMemberException(target.GetType().FullName, string.Join("|", memberNames));
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

        private static void AssertIntegerSetContainsAll(IReadOnlyList<long> expectedIds, IReadOnlyList<long> actualIds, string name)
        {
            HashSet<long> actualDistinctIds = actualIds.ToHashSet();
            foreach (long expectedId in expectedIds)
            {
                if (!actualDistinctIds.Contains(expectedId))
                    throw new InvalidDataException($"{name}: missing id {expectedId}.");
            }
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

        private static void AssertObjectListEmpty(object? value, string name)
        {
            List<object> list = ReadObjectList(value, name);
            if (list.Count != 0)
                throw new InvalidDataException($"{name}: expected an empty list, got {list.Count} entries.");
        }

        private static List<object> ReadObjectList(object? value, string name)
        {
            if (value is null)
                throw new InvalidDataException($"{name}: expected a list, got nil.");
            if (value is not System.Collections.IEnumerable values || value is string)
                throw new InvalidDataException($"{name}: expected a list.");

            List<object> result = new();
            foreach (object? item in values)
            {
                if (item is null)
                    throw new InvalidDataException($"{name}: expected non-nil entries.");
                result.Add(item);
            }

            return result;
        }

        private static System.Collections.IDictionary RequiredDynamicMap(object? value, string name)
        {
            if (value is System.Collections.IDictionary map)
                return map;

            string actualType = value?.GetType().FullName ?? "nil";
            throw new InvalidDataException($"{name}: expected a dynamic map, got {actualType}.");
        }

        private static object RequiredDynamicValue(System.Collections.IDictionary map, string key, string name)
        {
            if (!map.Contains(key))
                throw new InvalidDataException($"{name}: expected dynamic field {key}.");

            return map[key] ?? throw new InvalidDataException($"{name}.{key}: expected non-nil value.");
        }

        private static int RequiredDynamicInteger(System.Collections.IDictionary map, string key, string name)
        {
            return Convert.ToInt32(RequiredDynamicValue(map, key, name));
        }

        private static bool RequiredDynamicBoolean(System.Collections.IDictionary map, string key, string name)
        {
            object value = RequiredDynamicValue(map, key, name);
            if (value is bool boolean)
                return boolean;

            throw new InvalidDataException($"{name}.{key}: expected a boolean, got {value.GetType().FullName}.");
        }

        private static List<object> RequiredDynamicObjectList(System.Collections.IDictionary map, string key, string name)
        {
            return ReadObjectList(RequiredDynamicValue(map, key, name), name);
        }

        private static MemberInfo? OptionalDataMember(Type type, string memberName)
        {
            MemberInfo[] members = type.GetMember(memberName, BindingFlags.Instance | BindingFlags.Public)
                .Where(member => member is FieldInfo || member is PropertyInfo { GetMethod: not null })
                .ToArray();

            return members.Length switch
            {
                0 => null,
                1 => members[0],
                _ => throw new AmbiguousMatchException($"{type.FullName}.{memberName} matched {members.Length} members.")
            };
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
                case OperandType.ShortInlineR:
                    offset += 4;
                    return BitConverter.ToSingle(il, operandOffset);
                case OperandType.InlineR:
                    offset += 8;
                    return BitConverter.ToDouble(il, operandOffset);
                case OperandType.ShortInlineVar:
                    offset += 1;
                    return (int)il[operandOffset];
                case OperandType.InlineVar:
                    offset += 2;
                    return BitConverter.ToUInt16(il, operandOffset);
                case OperandType.ShortInlineBrTarget:
                    offset += 1;
                    return offset + (sbyte)il[operandOffset];
                case OperandType.InlineBrTarget:
                    offset += 4;
                    return offset + BitConverter.ToInt32(il, operandOffset);
                case OperandType.InlineSwitch:
                    int switchCount = BitConverter.ToInt32(il, operandOffset);
                    offset += 4;
                    int[] switchTargets = new int[switchCount];
                    int switchBaseOffset = offset + (switchCount * 4);
                    for (int targetIndex = 0; targetIndex < switchCount; targetIndex++)
                    {
                        switchTargets[targetIndex] = switchBaseOffset + BitConverter.ToInt32(il, offset);
                        offset += 4;
                    }
                    return switchTargets;
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

        private static double? LdcR8Value(IlInstruction instruction)
        {
            if (instruction.OpCode == OpCodes.Ldc_R8 && instruction.Operand is double doubleValue)
                return doubleValue;
            if (instruction.OpCode == OpCodes.Ldc_R4 && instruction.Operand is float floatValue)
                return floatValue;

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
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
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
            foreach (JObject content in scrollPicNotice.Value<JArray>("Content")!.Cast<JObject>())
            {
                string picAddr = RequiredNonEmptyString(content, "PicAddr", "ScrollPicNotice content");
                string[] picPathParts = picAddr.Split('/');
                string picPath = ResourcePath(["Configs", "Notices", "4.5.0", .. picPathParts]);
                if (!File.Exists(picPath))
                    throw new FileNotFoundException($"ScrollPicNotice image fixture missing: {picAddr}", picPath);
            }

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
                    "/prod/client/notice/config/prod-encdn-tx/com.kurogame.pc.punishing.grayraven.en/4.5.0/ScrollPicNotice.json",
                ];

                foreach (string endpoint in endpoints)
                {
                    using HttpResponseMessage response = await client.GetAsync(endpoint);
                    string body = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidDataException($"{endpoint}: expected HTTP 200, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");

                    AssertCurrentClientNoticePayload(JObject.Parse(body), endpoint);
                }

                using HttpResponseMessage picResponse = await client.GetAsync("/prod/client/notice/pic/6a1e1534f1b4a13fd8bf4904.png");
                byte[] picBody = await picResponse.Content.ReadAsByteArrayAsync();
                if (picResponse.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException($"notice pic endpoint: expected HTTP 200, got {(int)picResponse.StatusCode} {picResponse.StatusCode}.");
                if (picBody.Length < 8 || picBody[0] != 0x89 || picBody[1] != 0x50 || picBody[2] != 0x4E || picBody[3] != 0x47)
                    throw new InvalidDataException("notice pic endpoint did not return PNG data.");

                (string endpoint, string title)[] noticeHtmlEndpoints = CurrentClientNoticeHtmlEndpoints().ToArray();
                AssertCurrentClientNoticeHtmlEndpointIsCovered(noticeHtmlEndpoints, "/prod/client/notice/html/6a1e0fcbf1b4a13fd8bf48ff.html");
                foreach ((string endpoint, string title) in noticeHtmlEndpoints)
                    await AssertCurrentClientNoticeHtmlEndpoint(client, endpoint, title);

                foreach (string missingEndpoint in new[]
                {
                    "/prod/client/notice/html/unknown-notice.html",
                    "/prod/client/notice/html/unknown-notice.txt",
                })
                {
                    using HttpResponseMessage missingResponse = await client.GetAsync(missingEndpoint);
                    if (missingResponse.StatusCode != HttpStatusCode.NotFound)
                    {
                        string missingBody = await missingResponse.Content.ReadAsStringAsync();
                        throw new InvalidDataException($"{missingEndpoint}: expected HTTP 404, got {(int)missingResponse.StatusCode} {missingResponse.StatusCode}. Body: {missingBody}");
                    }
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

        private static IEnumerable<(string Endpoint, string Title)> CurrentClientNoticeHtmlEndpoints()
        {
            JObject loginNotice = JObject.Parse(File.ReadAllText(ResourcePath("Configs", "Notices", "4.5.0", "LoginNotice.json")));
            yield return NoticeHtmlEndpoint(
                RequiredNonEmptyString(loginNotice, "HtmlUrl", "LoginNotice"),
                RequiredNonEmptyString(loginNotice, "Title", "LoginNotice"));

            JArray gameNotices = JArray.Parse(File.ReadAllText(ResourcePath("Configs", "Notices", "4.5.0", "GameNotice.json")));
            foreach (JObject notice in gameNotices.OfType<JObject>())
            {
                foreach (JObject content in (notice.Value<JArray>("Content") ?? new JArray()).OfType<JObject>())
                {
                    yield return NoticeHtmlEndpoint(
                        RequiredNonEmptyString(content, "Url", "GameNotice Content"),
                        RequiredNonEmptyString(content, "Title", "GameNotice Content"));
                }
            }
        }

        private static (string Endpoint, string Title) NoticeHtmlEndpoint(string htmlUrl, string title)
        {
            string[] parts = htmlUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string fileName = parts.Length == 0 ? string.Empty : parts[^1];
            if (!fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{htmlUrl}: expected notice HTML URL ending in .html.");

            return ($"/prod/client/notice/html/{fileName}", title);
        }

        private static void AssertCurrentClientNoticeHtmlEndpointIsCovered((string Endpoint, string Title)[] endpoints, string expectedEndpoint)
        {
            if (!endpoints.Any(endpoint => endpoint.Endpoint == expectedEndpoint))
                throw new InvalidDataException($"Current GameNotice fixture does not reference required notice HTML endpoint {expectedEndpoint}.");
        }

        private static async Task AssertCurrentClientNoticeHtmlEndpoint(HttpClient client, string endpoint, string title)
        {
            using HttpResponseMessage response = await client.GetAsync(endpoint);
            string body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException($"{endpoint}: expected HTTP 200, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{endpoint}: expected text/html Content-Type, got '{response.Content.Headers.ContentType}'.");

            if (body.Length == 0)
                throw new InvalidDataException($"{endpoint}: expected non-empty HTML body.");

            string encodedTitle = WebUtility.HtmlEncode(title);
            if (!body.Contains(encodedTitle, StringComparison.Ordinal))
                throw new InvalidDataException($"{endpoint}: expected HTML body to contain notice title '{title}'.");
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
            AssertEqual("0", ConfigValue(remoteConfigs, "IsHideFunc"), "IsHideFunc config");
            AssertEqual("0", ConfigValue(remoteConfigs, "IsHideFuncAndroid"), "IsHideFuncAndroid config");
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

        private static void AssertWorldChatSeed(AscNet.GameServer.Handlers.NotifyWorldChat notifyWorldChat, string name)
        {
            if (notifyWorldChat.ChatMessages is null || notifyWorldChat.ChatMessages.Count == 0)
                throw new InvalidDataException($"{name}: expected at least one cached chat message.");

            AscNet.GameServer.Handlers.ChatData seedMessage = notifyWorldChat.ChatMessages[0];
            AssertEqual(AscNet.GameServer.Handlers.ChatChannelType.World, seedMessage.ChannelType, $"{name} first ChatMessages ChannelType");
            AssertEqual(true, seedMessage.SenderId != 0, $"{name} first ChatMessages nonzero SenderId");
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
