using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.fashionstory;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Table.V2.share.miniactivity.dyemerge;
using AscNet.Table.V2.share.miniactivity.hitmouse;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.guide;
using AscNet.Table.V2.share.passport;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.wheelchairmanual;
using AscNet.Table.V2.share.lotto;
using TaskTable = AscNet.Table.V2.share.task.TaskTable;
using MessagePack;
using Newtonsoft.Json.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateVersion46ActivityCompatibility()
    {
        // Reuse the packet-level regression for process transitions, response state, persistence, and replay rejection.
        ValidateLifeTreeFinishProcessRequestCompatibility();
        ValidateWheelchairManualCompatibility();

        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForShopCompatibility();

        object Call(string typeName, string method, Type[] signature, params object?[] arguments)
        {
            Type type = RequiredAscNetGameServerType($"AscNet.GameServer.Handlers.{typeName}");
            return RequiredMethod(type, method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, signature)
                .Invoke(null, arguments) ?? throw new InvalidDataException($"{typeName}.{method} returned null.");
        }

        MethodInfo prepareTransfinite = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TransfiniteModule"),
            "PrepareLogin",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(long)]);
        List<(TransfiniteActivityTable Activity, ActivityScheduleEntry Schedule)> transfiniteRotations =
            TableReaderV2.Parse<TransfiniteActivityTable>()
                .Select(activity => ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry schedule)
                    ? (Activity: activity, Schedule: schedule)
                    : ((TransfiniteActivityTable Activity, ActivityScheduleEntry Schedule)?)null)
                .Where(pair => pair is not null && pair.Value.Schedule.Source.StartsWith("version-history:", StringComparison.Ordinal))
                .Select(pair => pair!.Value)
                .OrderBy(pair => pair.Activity.Id)
                .ToList();
        if (transfiniteRotations.Count != 2 || ActivityScheduleService.All.Any(schedule =>
                schedule.Source == "policy:latest-TransfiniteActivity-always-open"))
            throw new InvalidDataException("4.6 Transfinite must expose only the two version-history-derived rotations.");
        Player scheduledTransfinitePlayer = CreateDrawCompatibilityPlayer(46_101);
        void PrepareTransfinite(long timestamp) => prepareTransfinite.Invoke(null, [scheduledTransfinitePlayer, timestamp]);
        PrepareTransfinite(transfiniteRotations[0].Schedule.StartTime - 1);
        AssertEqual(null, scheduledTransfinitePlayer.Transfinite, "Transfinite is unauthorized before the first 4.6 rotation");
        PrepareTransfinite(transfiniteRotations[0].Schedule.StartTime);
        TransfiniteState firstTransfinite = scheduledTransfinitePlayer.Transfinite
            ?? throw new InvalidDataException("Transfinite did not authorize at its first schedule boundary.");
        AssertEqual(transfiniteRotations[0].Activity.Id, firstTransfinite.ActivityId, "Transfinite selects the first open activity");
        AssertEqual(
            transfiniteRotations[0].Schedule.StartTime - transfiniteRotations[0].Schedule.StartTime % transfiniteRotations[0].Activity.CycleSeconds,
            firstTransfinite.BeginTime,
            "Transfinite derives the first rotation from its own CycleSeconds");
        PrepareTransfinite(transfiniteRotations[1].Schedule.StartTime);
        TransfiniteState secondTransfinite = scheduledTransfinitePlayer.Transfinite
            ?? throw new InvalidDataException("Transfinite did not switch at its second schedule boundary.");
        AssertEqual(transfiniteRotations[1].Activity.Id, secondTransfinite.ActivityId, "Transfinite selects the highest open activity");
        AssertEqual(
            transfiniteRotations[1].Schedule.StartTime - transfiniteRotations[1].Schedule.StartTime % transfiniteRotations[1].Activity.CycleSeconds,
            secondTransfinite.BeginTime,
            "Transfinite derives the second rotation from its own CycleSeconds");
        PrepareTransfinite(transfiniteRotations[1].Schedule.EndTime);
        AssertEqual(0L, scheduledTransfinitePlayer.Transfinite!.ActivityAuthorizedUntil, "Transfinite clears authorization after its 4.6 windows");

        // With no authoritative time-window source, configured rows alone must never open either activity.
        Player inactive = CreateDrawCompatibilityPlayer(46_001);
        DyeMergeStagesRecordNotify inactiveDye = (DyeMergeStagesRecordNotify)Call(
            "DyeMergeModule", "BuildNotify", [typeof(Player)], inactive);
        AssertEqual(0, inactiveDye.ActivityId, "DyeMerge inactive activity boundary");
        NotifyHitMouseData inactiveMouse = (NotifyHitMouseData)Call(
            "HitMouseModule", "BuildNotifyHitMouseData", [typeof(Player)], inactive);
        AssertEqual(0, inactiveMouse.ActivityId, "HitMouse inactive activity boundary");

        // The authorized overload is table-derived: it reconciles stale state and emits sorted, unique completion state.
        DyeMergeActivityTable dyeActivity = TableReaderV2.Parse<DyeMergeActivityTable>()
            .First(row => row.ChapterIds.Count > 0);
        Player dyePlayer = CreateDrawCompatibilityPlayer(46_002);
        dyePlayer.DyeMerge = new DyeMergeState { ActivityId = dyeActivity.Id + 1, CompletedStageIds = [99, 99] };
        DyeMergeStagesRecordNotify reconciledDye = (DyeMergeStagesRecordNotify)Call(
            "DyeMergeModule", "BuildNotify", [typeof(Player), typeof(DyeMergeActivityTable)], dyePlayer, dyeActivity);
        AssertEqual(dyeActivity.Id, reconciledDye.ActivityId, "DyeMerge table activity id");
        AssertEqual(0, reconciledDye.StageRecord.Count, "DyeMerge cross-activity state isolation");
        DyeMergeChapterTable dyeChapter = TableReaderV2.Parse<DyeMergeChapterTable>()
            .First(row => dyeActivity.ChapterIds.Contains(row.Id) && row.StageIds.Count > 0);
        DyeMergeStageTable firstDyeStage = TableReaderV2.Parse<DyeMergeStageTable>()
            .First(row => row.Id == dyeChapter.StageIds[0]);
        object?[] completeArgs = [dyePlayer, dyeActivity, firstDyeStage.Id, false];
        int firstDyeCode = (int)Call("DyeMergeModule", "TryComplete",
            [typeof(Player), typeof(DyeMergeActivityTable), typeof(int), typeof(bool).MakeByRefType()], completeArgs);
        if (firstDyeStage.PreStage is null or 0 && dyeChapter.Condition is null or 0)
        {
            AssertEqual(0, firstDyeCode, "DyeMerge first table stage completion");
            AssertEqual(true, (bool)completeArgs[3]!, "DyeMerge first completion inserted");
            completeArgs = [dyePlayer, dyeActivity, firstDyeStage.Id, false];
            AssertEqual(0, (int)Call("DyeMergeModule", "TryComplete",
                [typeof(Player), typeof(DyeMergeActivityTable), typeof(int), typeof(bool).MakeByRefType()], completeArgs),
                "DyeMerge repeated completion response");
            AssertEqual(false, (bool)completeArgs[3]!, "DyeMerge repeated completion idempotence");
        }

        // HitMouse derives unlock cost/predecessor from tables, preserves the maximum score, and uses zero-based claims.
        List<HitMouseStageTable> allMouseStages = TableReaderV2.Parse<HitMouseStageTable>().ToList();
        HitMouseActivityTable mouseActivity = TableReaderV2.Parse<HitMouseActivityTable>()
            .First(activity => allMouseStages.Any(stage => stage.ActivityId == activity.Id));
        List<HitMouseStageTable> mouseStages = allMouseStages
            .Where(row => row.ActivityId == mouseActivity.Id).ToList();
        HitMouseStageTable mouseStage = mouseStages.First(row => row.PreStageId is null or 0);
        HitMouseState mouseState = new() { ActivityId = mouseActivity.Id };
        Inventory mouseInventory = CreateDrawCompatibilityInventory(46_003,
            [new Item { Id = mouseActivity.UseItem, Count = mouseStage.UnlockItemCount }]);
        object?[] unlockArgs = [mouseState, mouseActivity, mouseStage, mouseInventory, null];
        AssertEqual(0, (int)Call("HitMouseModule", "TryUnlock",
            [typeof(HitMouseState), typeof(HitMouseActivityTable), typeof(HitMouseStageTable), typeof(Inventory), typeof(Item).MakeByRefType()], unlockArgs),
            "HitMouse table-backed unlock");
        AssertEqual(0L, mouseInventory.Items.Single(item => item.Id == mouseActivity.UseItem).Count,
            "HitMouse table-backed unlock cost");
        AssertEqual(0, (int)Call("HitMouseModule", "TryFinish", [typeof(HitMouseState), typeof(int), typeof(int)], mouseState, mouseStage.Id, 17), "HitMouse finish");
        AssertEqual(0, (int)Call("HitMouseModule", "TryFinish", [typeof(HitMouseState), typeof(int), typeof(int)], mouseState, mouseStage.Id, 3), "HitMouse lower replay");
        AssertEqual(17, mouseState.LevelScores[mouseStage.Id], "HitMouse maximum score retention");
        List<int> eligible = (List<int>)Call("HitMouseModule", "EligibleRewardIndices",
            [typeof(HitMouseState), typeof(IReadOnlyList<int>)], mouseState, new List<int> { 0, 18 });
        AssertIntegerList([0], eligible.Select(Convert.ToInt64).ToArray(), "HitMouse zero-based reward claim");
        mouseState.ClaimedRewardIndices.Add(0);
        AssertEqual(0, ((List<int>)Call("HitMouseModule", "EligibleRewardIndices",
            [typeof(HitMouseState), typeof(IReadOnlyList<int>)], mouseState, new List<int> { 0, 18 })).Count,
            "HitMouse one-time reward claim");

        // Fashion authorization is rebuilt from the current activity, schedule, and its function gate.
        FashionStoryTable fashionActivity = TableReaderV2.Parse<FashionStoryTable>()
            .Single(row => row.TimeId == 48820);
        DateTimeOffset fashionNow = DateTimeOffset.FromUnixTimeSeconds(1784242800);
        Player fashionPlayer = CreateDrawCompatibilityPlayer(46_004);
        fashionPlayer.PlayerData.Level = 40;
        using LoopbackSessionHarness fashionHarness = new(CreateDrawCompatibilityCharacter(46_004), fashionPlayer,
            CreateDrawCompatibilityInventory(46_004, []), sessionId: "version-46-fashion");
        fashionHarness.Session.stage = CreateLoginAccountCompatibilityStage(46_004);
        AssertEqual(true, (bool)Call("FashionStoryModule", "PrepareLogin",
            [typeof(Player), typeof(DateTimeOffset)], fashionPlayer, fashionNow), "Fashion story fresh authorization");
        AssertEqual(false, (bool)Call("FashionStoryModule", "PrepareLogin",
            [typeof(Player), typeof(DateTimeOffset)], fashionPlayer, fashionNow), "Fashion story relog idempotence");
        NotifyFashionStoryData authorizedFashion = (NotifyFashionStoryData)Call(
            "FashionStoryModule", "BuildNotify", [typeof(Session)], fashionHarness.Session);
        AssertEqual(fashionActivity.Id, authorizedFashion.ActivityId, "Fashion story authorized activity");
        AssertEqual(true, (bool)Call("FashionStoryModule", "IsTrialStage", [typeof(Session), typeof(int)],
            fashionHarness.Session, fashionActivity.TrialStages[0]), "Fashion trial route");
        FashionStoryState fashionRoundTrip = BsonSerializer.Deserialize<Player>(fashionPlayer.ToBson()).FashionStory!;
        AssertEqual(fashionActivity.Id, fashionRoundTrip.AuthorizedActivityId, "Fashion authorization relog state");

        Player lowLevelFashionPlayer = CreateDrawCompatibilityPlayer(46_104);
        lowLevelFashionPlayer.PlayerData.Level = 39;
        AssertEqual(false, (bool)Call("FashionStoryModule", "PrepareLogin",
            [typeof(Player), typeof(DateTimeOffset)], lowLevelFashionPlayer, fashionNow), "Fashion story level gate");
        AssertEqual(null, lowLevelFashionPlayer.FashionStory, "Fashion story closed level gate does not mutate state");
        AssertEqual(true, (bool)Call("FashionStoryModule", "PrepareLogin",
            [typeof(Player), typeof(DateTimeOffset)], fashionPlayer, fashionNow.AddSeconds(-1)), "Fashion story schedule closure");
        AssertEqual(0, fashionPlayer.FashionStory!.AuthorizedActivityId, "Fashion story closed authorization");
        AssertEqual(0, fashionPlayer.FashionStory.AuthorizedTimeIds.Count, "Fashion story closed TimeIds");

        // LifeTree/passport debt state must survive BSON without duplicate or reordered claim markers.
        Player debt = CreateDrawCompatibilityPlayer(46_005);
        debt.LifeTreeData.UnlockCharacterData[7] = new LifeTreeUnlockCharacterData { Id = 7, UnlockStatus = 2 };
        debt.LifeTreeData.FinishedChapters = [3, 8];
        debt.Passport = new PassportState
        {
            ActivityId = 0,
            PassportInfos = [new PassportStateInfo { Id = 2, GotRewardList = [3, 1, 1] }],
            IsGetSupplyReward = false
        };
        Player debtReload = BsonSerializer.Deserialize<Player>(debt.ToBson());
        AssertEqual(2, debtReload.LifeTreeData.UnlockCharacterData[7].UnlockStatus, "LifeTree unlock relog state");
        AssertIntegerList([3, 8], debtReload.LifeTreeData.FinishedChapters.Select(Convert.ToInt64).ToArray(), "LifeTree process relog state");
        AssertEqual(0, debtReload.Passport.ActivityId, "Passport absent activity boundary");
        AssertEqual(false, debtReload.Passport.IsGetSupplyReward, "Passport absent supply boundary");
        AssertIntegerList([3, 1, 1], debtReload.Passport.PassportInfos.Single().GotRewardList.Select(Convert.ToInt64).ToArray(),
            "Passport persisted claim ordering");

        Dictionary<int, ConditionTable> guideConditions = TableReaderV2.Parse<ConditionTable>()
            .ToDictionary(row => row.Id);
        GuideGroupTable rewardedGuide = TableReaderV2.Parse<GuideGroupTable>()
            .First(row => row.RewardId > 0
                && row.ConditionId.Count == 1
                && guideConditions.TryGetValue(row.ConditionId[0], out ConditionTable? condition)
                && condition.Type == 10105
                && condition.Params.Count == 1);
        ConditionTable rewardedGuideCondition = guideConditions[rewardedGuide.ConditionId[0]];
        RewardTable rewardedGuideTable = TableReaderV2.Parse<RewardTable>()
            .Single(row => row.Id == rewardedGuide.RewardId);
        HashSet<int> rewardedGuideSubIds = rewardedGuideTable.SubIds.ToHashSet();
        List<RewardGoodsTable> rewardedGuideGoods = TableReaderV2.Parse<RewardGoodsTable>()
            .Where(row => rewardedGuideSubIds.Contains(row.Id))
            .ToList();
        AssertEqual(true, rewardedGuideGoods.Count > 0,
            "Guide retry test has configured rewards");
        int rewardedGuideStageId = rewardedGuideCondition.Params.Single();

        using (MongoCollectionOverride guideRewardOverride =
               MongoCollectionOverride.InstallForDailySignInCompatibility(
                   out RecordingMongoCollectionProxy<Player> guidePlayerCollection,
                   out RecordingMongoCollectionProxy<Character> guideCharacterCollection,
                   out RecordingMongoCollectionProxy<Inventory> guideInventoryCollection))
        {
            const long guidePlayerId = 46_006;
            Player guidePlayer = CreateDrawCompatibilityPlayer(guidePlayerId);
            Inventory guideInventory = CreateDrawCompatibilityInventory(guidePlayerId, []);
            Character guideCharacter = CreateDrawCompatibilityCharacter(guidePlayerId);
            using LoopbackSessionHarness guideHarness = new(
                guideCharacter,
                guidePlayer,
                guideInventory,
                "guide-reward-retry-compat-test");
            guideHarness.Session.stage = CreateLoginAccountCompatibilityStage(guidePlayerId);
            guideHarness.Session.stage.Stages[rewardedGuideStageId] = new StageDatum
            {
                StageId = checked((uint)rewardedGuideStageId),
                Passed = true
            };
            guidePlayerCollection.ThrowOnReplaceOne = true;

            InvokeRegisteredRequestHandler(
                nameof(GuideCompleteRequest),
                guideHarness.Session,
                46_600,
                new GuideCompleteRequest { GuideGroupId = rewardedGuide.Id });
            guidePlayerCollection.ThrowOnReplaceOne = false;
            GuideCompleteResponse failedGuide = ReadResponsePayload<GuideCompleteResponse>(
                guideHarness,
                46_600,
                nameof(GuideCompleteResponse),
                "Guide reward player persistence failure response");
            AssertEqual(1, failedGuide.Code, "Guide reward player persistence failure Code");
            AssertNoAvailablePacket(guideHarness, "Guide reward player persistence failure");
            AssertEqual(false,
                guidePlayer.PlayerData.GuideData?.Contains(rewardedGuide.Id) == true,
                "Guide reward player persistence failure rolls back completion");
            string guideClaimKey = $"guide:{rewardedGuide.Id}";
            AssertEqual(true,
                guideInventory.AppliedRewardClaims.Contains(
                    guideClaimKey,
                    StringComparer.Ordinal),
                "Guide reward player persistence failure records inventory receipt");
            AssertEqual(true,
                guideCharacter.AppliedRewardClaims.Contains(
                    guideClaimKey,
                    StringComparer.Ordinal),
                "Guide reward player persistence failure records character receipt");
            Dictionary<int, long> guideCountsAfterFailure = rewardedGuideGoods
                .Select(row => row.TemplateId)
                .Distinct()
                .ToDictionary(
                    templateId => templateId,
                    templateId => guideInventory.Items
                        .FirstOrDefault(item => item.Id == templateId)?.Count ?? 0);
            AssertEqual(1, guideInventoryCollection.ReplaceOneCalls,
                "Guide reward player persistence failure saves inventory once");
            AssertEqual(1, guideCharacterCollection.ReplaceOneCalls,
                "Guide reward player persistence failure saves character once");
            guideHarness.Session.stage.Stages.Remove(rewardedGuideStageId);

            InvokeRegisteredRequestHandler(
                nameof(GuideCompleteRequest),
                guideHarness.Session,
                46_601,
                new GuideCompleteRequest { GuideGroupId = rewardedGuide.Id });
            NotifyGuide? retriedGuideNotify = null;
            for (int packetIndex = 0; packetIndex < 8; packetIndex++)
            {
                Packet packet = guideHarness.ReadPacket("Guide reward persistence retry packet");
                AssertEqual(Packet.ContentType.Push, packet.Type, "Guide reward persistence retry push packet type");
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                if (push.Name != nameof(NotifyGuide))
                    continue;
                retriedGuideNotify = MessagePackSerializer.Deserialize<NotifyGuide>(push.Content);
                break;
            }
            if (retriedGuideNotify is null)
                throw new InvalidDataException("Guide reward persistence retry did not notify guide completion.");
            AssertEqual(rewardedGuide.Id,
                retriedGuideNotify.GuideGroupId,
                "Guide reward persistence retry notified guide");
            GuideCompleteResponse retriedGuide = (GuideCompleteResponse)ReadResponsePayload(
                guideHarness,
                46_601,
                nameof(GuideCompleteResponse),
                "Guide reward persistence retry response",
                typeof(GuideCompleteResponse),
                maxPacketsToRead: 8);
            AssertEqual(0, retriedGuide.Code, "Guide reward persistence retry succeeds");
            AssertEqual(rewardedGuideGoods.Count, retriedGuide.RewardGoodsList?.Count ?? 0,
                "Guide reward persistence retry returns configured rewards");
            AssertEqual(true,
                guidePlayer.PlayerData.GuideData?.Contains(rewardedGuide.Id) == true,
                "Guide reward persistence retry records completion");
            foreach ((int templateId, long count) in guideCountsAfterFailure)
            {
                AssertEqual(count,
                    guideInventory.Items.FirstOrDefault(item => item.Id == templateId)?.Count ?? 0,
                    $"Guide reward persistence retry does not duplicate item {templateId}");
            }
            AssertEqual(1, guideInventoryCollection.ReplaceOneCalls,
                "Guide reward persistence retry reuses inventory receipt");
            AssertEqual(1, guideCharacterCollection.ReplaceOneCalls,
                "Guide reward persistence retry reuses character receipt");
        }

        PassportActivityTable passportActivity = TableReaderV2.Parse<PassportActivityTable>()
            .First(row => TableReaderV2.Parse<PassportLevelTable>()
                .Any(level => level.ActivityId == row.Id
                    && level.Level == 1
                    && (level.TotalExp ?? 0) == 0));
        PassportTypeInfoTable passportType = TableReaderV2.Parse<PassportTypeInfoTable>()
            .First(row => row.ActivityId == passportActivity.Id);
        PassportRewardTable passportReward = TableReaderV2.Parse<PassportRewardTable>()
            .First(row => row.PassportId == passportType.Id
                && row.Level == 1
                && row.RewardId > 0);
        RewardTable passportRewardTable = TableReaderV2.Parse<RewardTable>()
            .Single(row => row.Id == passportReward.RewardId!.Value);
        HashSet<int> passportRewardSubIds = passportRewardTable.SubIds.ToHashSet();
        List<RewardGoodsTable> passportRewardGoods = TableReaderV2.Parse<RewardGoodsTable>()
            .Where(row => passportRewardSubIds.Contains(row.Id))
            .ToList();
        AssertEqual(true, passportRewardGoods.Count > 0,
            "Passport retry test has configured rewards");

        using (MongoCollectionOverride passportRewardOverride =
               MongoCollectionOverride.InstallForDailySignInCompatibility(
                   out RecordingMongoCollectionProxy<Player> passportPlayerCollection,
                   out RecordingMongoCollectionProxy<Character> passportCharacterCollection,
                   out RecordingMongoCollectionProxy<Inventory> passportInventoryCollection))
        {
            const long passportPlayerId = 46_007;
            Player passportPlayer = CreateDrawCompatibilityPlayer(passportPlayerId);
            passportPlayer.Passport = new PassportState
            {
                ActivityId = passportActivity.Id,
                PassportInfos = [new PassportStateInfo { Id = passportType.Id }]
            };
            Inventory passportInventory = CreateDrawCompatibilityInventory(passportPlayerId, []);
            Character passportCharacter = CreateDrawCompatibilityCharacter(passportPlayerId);
            using LoopbackSessionHarness passportHarness = new(
                passportCharacter,
                passportPlayer,
                passportInventory,
                "passport-reward-retry-compat-test");
            passportPlayerCollection.ThrowOnReplaceOne = true;

            InvokeRegisteredRequestHandler(
                nameof(PassportRecvAllRewardRequest),
                passportHarness.Session,
                46_602,
                new PassportRecvAllRewardRequest());
            passportPlayerCollection.ThrowOnReplaceOne = false;
            PassportRecvAllRewardResponse failedPassport =
                ReadResponsePayload<PassportRecvAllRewardResponse>(
                    passportHarness,
                    46_602,
                    nameof(PassportRecvAllRewardResponse),
                    "Passport reward player persistence failure response");
            AssertEqual(20137010, failedPassport.Code,
                "Passport reward player persistence failure Code");
            AssertEqual(false,
                passportPlayer.Passport.PassportInfos.Single().GotRewardList
                    .Contains(passportReward.Id),
                "Passport reward player persistence failure rolls back claim marker");
            string passportClaimKey =
                $"passport-reward:{passportActivity.Id}:{passportType.Id}:{passportReward.Id}";
            AssertEqual(true,
                passportInventory.AppliedRewardClaims.Contains(
                    passportClaimKey,
                    StringComparer.Ordinal),
                "Passport reward player persistence failure records inventory receipt");
            AssertEqual(true,
                passportCharacter.AppliedRewardClaims.Contains(
                    passportClaimKey,
                    StringComparer.Ordinal),
                "Passport reward player persistence failure records character receipt");
            Dictionary<int, long> passportCountsAfterFailure = passportRewardGoods
                .Select(row => row.TemplateId)
                .Distinct()
                .ToDictionary(
                    templateId => templateId,
                    templateId => passportInventory.Items
                        .FirstOrDefault(item => item.Id == templateId)?.Count ?? 0);
            AssertEqual(1, passportInventoryCollection.ReplaceOneCalls,
                "Passport reward player persistence failure saves inventory once");
            AssertEqual(1, passportCharacterCollection.ReplaceOneCalls,
                "Passport reward player persistence failure saves character once");

            InvokeRegisteredRequestHandler(
                nameof(PassportRecvAllRewardRequest),
                passportHarness.Session,
                46_603,
                new PassportRecvAllRewardRequest());
            PassportRecvAllRewardResponse retriedPassport =
                (PassportRecvAllRewardResponse)ReadResponsePayload(
                    passportHarness,
                    46_603,
                    nameof(PassportRecvAllRewardResponse),
                    "Passport reward persistence retry response",
                    typeof(PassportRecvAllRewardResponse),
                    maxPacketsToRead: 8);
            AssertEqual(0, retriedPassport.Code,
                "Passport reward persistence retry succeeds");
            AssertEqual(passportRewardGoods.Count, retriedPassport.RewardList.Count,
                "Passport reward persistence retry returns configured rewards");
            AssertEqual(true,
                passportPlayer.Passport.PassportInfos.Single().GotRewardList
                    .Contains(passportReward.Id),
                "Passport reward persistence retry records claim marker");
            foreach ((int templateId, long count) in passportCountsAfterFailure)
            {
                AssertEqual(count,
                    passportInventory.Items.FirstOrDefault(item => item.Id == templateId)?.Count ?? 0,
                    $"Passport reward persistence retry does not duplicate item {templateId}");
            }
            AssertEqual(1, passportInventoryCollection.ReplaceOneCalls,
                "Passport reward persistence retry reuses inventory receipt");
            AssertEqual(1, passportCharacterCollection.ReplaceOneCalls,
                "Passport reward persistence retry reuses character receipt");
        }

        const long duplicatePassportPlayerId = 46_008;
        Player duplicatePassportPlayer = CreateDrawCompatibilityPlayer(duplicatePassportPlayerId);
        duplicatePassportPlayer.Passport = new PassportState
        {
            ActivityId = passportActivity.Id,
            PassportInfos =
            [
                new PassportStateInfo { Id = passportType.Id },
                new PassportStateInfo { Id = passportType.Id }
            ]
        };
        using (LoopbackSessionHarness duplicatePassportHarness = new(
                   CreateDrawCompatibilityCharacter(duplicatePassportPlayerId),
                   duplicatePassportPlayer,
                   CreateDrawCompatibilityInventory(duplicatePassportPlayerId, []),
                   "passport-duplicate-tier-compat-test"))
        {
            InvokeRegisteredRequestHandler(
                nameof(PassportRecvAllRewardRequest),
                duplicatePassportHarness.Session,
                46_604,
                new PassportRecvAllRewardRequest());
            PassportRecvAllRewardResponse duplicatePassportResponse =
                ReadResponsePayload<PassportRecvAllRewardResponse>(
                    duplicatePassportHarness,
                    46_604,
                    nameof(PassportRecvAllRewardResponse),
                    "Passport duplicate tier response");
            AssertEqual(20137005, duplicatePassportResponse.Code,
                "Passport duplicate tier invalid-info Code");
        }

        List<FashionColorTable> fashionColors = TableReaderV2.Parse<FashionColorTable>();
        HashSet<int> fashionColorIds = fashionColors.Select(row => row.Id).ToHashSet();
        Dictionary<int, RewardTable> rewardsById = TableReaderV2.Parse<RewardTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, RewardGoodsTable> rewardGoodsById =
            TableReaderV2.Parse<RewardGoodsTable>().ToDictionary(row => row.Id);
        PassportRewardTable fashionColorPassportReward =
            TableReaderV2.Parse<PassportRewardTable>().First(row =>
                row.RewardId is > 0
                && rewardsById.TryGetValue(row.RewardId.Value, out RewardTable? reward)
                && reward.SubIds.Any(subId =>
                    rewardGoodsById.TryGetValue(subId, out RewardGoodsTable? goods)
                    && fashionColorIds.Contains(goods.TemplateId)));
        PassportTypeInfoTable fashionColorPassportType =
            TableReaderV2.Parse<PassportTypeInfoTable>()
                .Single(row => row.Id == fashionColorPassportReward.PassportId);
        PassportActivityTable fashionColorPassportActivity =
            TableReaderV2.Parse<PassportActivityTable>()
                .Single(row => row.Id == fashionColorPassportType.ActivityId);
        PassportLevelTable fashionColorPassportLevel =
            TableReaderV2.Parse<PassportLevelTable>()
                .First(row => row.ActivityId == fashionColorPassportActivity.Id
                    && row.Level == fashionColorPassportReward.Level);
        RewardTable fashionColorReward =
            rewardsById[fashionColorPassportReward.RewardId!.Value];
        RewardGoodsTable fashionColorGood = fashionColorReward.SubIds
            .Select(subId => rewardGoodsById[subId])
            .Single(goods => fashionColorIds.Contains(goods.TemplateId));
        FashionColorTable rewardedFashionColor =
            fashionColors.Single(row => row.Id == fashionColorGood.TemplateId);
        List<int> priorFashionColorTierRewards = TableReaderV2.Parse<PassportRewardTable>()
            .Where(row =>
                row.PassportId == fashionColorPassportType.Id
                && row.Level <= fashionColorPassportReward.Level
                && row.RewardId > 0
                && row.Id != fashionColorPassportReward.Id)
            .Select(row => row.Id)
            .ToList();

        using (MongoCollectionOverride fashionColorRewardOverride =
               MongoCollectionOverride.InstallForDailySignInCompatibility(
                   out RecordingMongoCollectionProxy<Player> fashionColorPlayerCollection,
                   out RecordingMongoCollectionProxy<Character> fashionColorCharacterCollection,
                   out RecordingMongoCollectionProxy<Inventory> fashionColorInventoryCollection))
        {
            const long fashionColorPlayerId = 46_009;
            Player fashionColorPlayer = CreateDrawCompatibilityPlayer(fashionColorPlayerId);
            fashionColorPlayer.Passport = new PassportState
            {
                ActivityId = fashionColorPassportActivity.Id,
                PassportInfos =
                [
                    new PassportStateInfo
                    {
                        Id = fashionColorPassportType.Id,
                        GotRewardList = priorFashionColorTierRewards
                    }
                ]
            };
            Inventory fashionColorInventory = CreateDrawCompatibilityInventory(
                fashionColorPlayerId,
                [
                    new Item
                    {
                        Id = Inventory.PassportExp,
                        Count = fashionColorPassportLevel.TotalExp ?? 0
                    }
                ]);
            Character fashionColorCharacter =
                CreateDrawCompatibilityCharacter(fashionColorPlayerId);
            using LoopbackSessionHarness fashionColorHarness = new(
                fashionColorCharacter,
                fashionColorPlayer,
                fashionColorInventory,
                "passport-fashion-color-reward-compat-test");

            InvokeRegisteredRequestHandler(
                nameof(PassportRecvAllRewardRequest),
                fashionColorHarness.Session,
                46_605,
                new PassportRecvAllRewardRequest());
            FashionSyncNotify fashionColorPush = ReadPushPayload<FashionSyncNotify>(
                fashionColorHarness,
                nameof(FashionSyncNotify),
                "Passport fashion-color reward push");
            PassportRecvAllRewardResponse fashionColorResponse =
                ReadResponsePayload<PassportRecvAllRewardResponse>(
                    fashionColorHarness,
                    46_605,
                    nameof(PassportRecvAllRewardResponse),
                    "Passport fashion-color reward response");

            AssertEqual(0, fashionColorResponse.Code,
                "Passport fashion-color reward Code");
            PassportRewardGoods colorRewardDto = fashionColorResponse.RewardList.Single();
            AssertEqual(fashionColorGood.TemplateId, colorRewardDto.TemplateId,
                "Passport fashion-color reward template");
            AssertEqual((int)RewardType.FashionColor, colorRewardDto.RewardType,
                "Passport fashion-color reward type");
            AssertIntegerList(
                [rewardedFashionColor.Id],
                fashionColorPush.FashionColors[rewardedFashionColor.OriginalFashionId]
                    .Select(Convert.ToInt64)
                    .ToArray(),
                "Passport fashion-color reward push ownership");
            AssertIntegerList(
                [rewardedFashionColor.Id],
                fashionColorCharacter.FashionColors[rewardedFashionColor.OriginalFashionId]
                    .Select(Convert.ToInt64)
                    .ToArray(),
                "Passport fashion-color persisted ownership");
            Character reloadedFashionColorCharacter =
                BsonSerializer.Deserialize<Character>(fashionColorCharacter.ToBson());
            AssertIntegerList(
                [rewardedFashionColor.Id],
                reloadedFashionColorCharacter.FashionColors[rewardedFashionColor.OriginalFashionId]
                    .Select(Convert.ToInt64)
                    .ToArray(),
                "Passport fashion-color BSON ownership");
            AssertEqual(1, fashionColorPlayerCollection.ReplaceOneCalls,
                "Passport fashion-color player save count");
            AssertEqual(1, fashionColorCharacterCollection.ReplaceOneCalls,
                "Passport fashion-color character save count");
            AssertEqual(1, fashionColorInventoryCollection.ReplaceOneCalls,
                "Passport fashion-color inventory receipt save count");
        }

        // Integer-key dictionaries use BSON array-of-documents and round-trip two distinct derived states.
        foreach (Dictionary<int, int> bestTimes in new[]
        {
            new Dictionary<int, int> { [9] = 120, [2] = 45 },
            new Dictionary<int, int> { [101] = 1 }
        })
        {
            Player transfinitePlayer = CreateDrawCompatibilityPlayer(46_100 + bestTimes.Count);
            transfinitePlayer.Transfinite = new TransfiniteState
            {
                ActivityId = 1,
                ActivityAuthorizedUntil = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                BestSpendTime = bestTimes,
                RotateSettleInfo = new TransfiniteRotateSettleState
                {
                    RotationId = 77,
                    MaxStageProgressIndex = 4,
                    SettleTransfiniteScore = 900,
                    UnSettleTransfiniteScore = 10,
                    GotScoreRewardIndex = [1]
                }
            };
            BsonDocument bson = transfinitePlayer.ToBsonDocument();
            Player loaded = BsonSerializer.Deserialize<Player>(bson);
            AssertEqual(string.Join(",", bestTimes.OrderBy(pair => pair.Key)),
                string.Join(",", loaded.Transfinite!.BestSpendTime.OrderBy(pair => pair.Key)),
                "Transfinite BSON integer-key roundtrip");
            loaded.Transfinite.LastRotateReceipt = new TransfiniteRotateSettleReceipt
            {
                RotationId = loaded.Transfinite.RotateSettleInfo!.RotationId,
                MaxStageProgressIndex = loaded.Transfinite.RotateSettleInfo.MaxStageProgressIndex,
                SettleTransfiniteScore = loaded.Transfinite.RotateSettleInfo.SettleTransfiniteScore,
                UnSettleTransfiniteScore = loaded.Transfinite.RotateSettleInfo.UnSettleTransfiniteScore
            };
            loaded.Transfinite.RotateSettleInfo = null;
            Player settledReload = BsonSerializer.Deserialize<Player>(loaded.ToBson());
            AssertEqual(null, settledReload.Transfinite!.RotateSettleInfo, "Transfinite pending settlement consumed exactly once");
            AssertEqual(77L, settledReload.Transfinite.LastRotateReceipt!.RotationId, "Transfinite settlement receipt relog state");
        }

        TransfiniteActivityTable settleActivity = TableReaderV2.Parse<TransfiniteActivityTable>().First();
        HashSet<int> configuredRegions = TableReaderV2.Parse<TransfiniteRegionTable>()
            .Select(row => row.RegionId)
            .ToHashSet();
        Dictionary<int, RewardTable> rewardRows = TableReaderV2.Parse<RewardTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, RewardGoodsTable> rewardGoodsRows = TableReaderV2.Parse<RewardGoodsTable>()
            .ToDictionary(row => row.Id);
        HashSet<int> itemIds = TableReaderV2.Parse<ItemTable>().Select(row => row.Id).ToHashSet();
        List<RewardGoodsTable> ResolveReward(int rewardId) =>
            rewardRows.TryGetValue(rewardId, out RewardTable? reward)
                ? reward.SubIds.Select(id => rewardGoodsRows.GetValueOrDefault(id)).OfType<RewardGoodsTable>().ToList()
                : [];
        List<TransfiniteScoreRewardGroupTable> settleRewardGroups =
            TableReaderV2.Parse<TransfiniteScoreRewardGroupTable>()
                .Where(group => configuredRegions.Contains(group.RegionId))
                .ToList();
        List<RewardGoodsTable> allConfiguredSettleRewards = settleRewardGroups
            .SelectMany(group => group.RewardId)
            .SelectMany(ResolveReward)
            .ToList();
        AssertEqual(true, allConfiguredSettleRewards.Count > 0,
            "Transfinite configured settlement rewards resolve");
        AssertEqual(true, allConfiguredSettleRewards.All(good => itemIds.Contains(good.TemplateId) && good.Count > 0),
            "Transfinite configured settlement rewards are positive-count items");

        (TransfiniteScoreRewardGroupTable Group, int Index, List<RewardGoodsTable> Goods)
            rewardCandidate = settleRewardGroups
                .SelectMany(group => group.RewardId.Select((rewardId, index) =>
                    (Group: group, Index: index, Goods: ResolveReward(rewardId))))
                .First(candidate => candidate.Group.ScoreRewardGroupId.HasValue
                    && candidate.Index < candidate.Group.Score.Count
                    && candidate.Goods.Count == 1
                    && candidate.Goods.All(good => itemIds.Contains(good.TemplateId) && good.Count > 0));
        using MongoCollectionOverride transfiniteMongoOverride =
            MongoCollectionOverride.InstallForTransfiniteCompatibility(
                out RecordingMongoCollectionProxy<Player> transfinitePlayerCollection,
                out RecordingMongoCollectionProxy<Inventory> transfiniteInventoryCollection);
        const long transfiniteLoginNow = 1784318400;
        MethodInfo prepareTransfiniteLogin = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TransfiniteModule"),
            "PrepareLogin",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(long)]);
        foreach ((int level, int expectedRegion, long playerId) in new[]
        {
            (80, 1, 46_108L),
            (100, 2, 46_109L)
        })
        {
            Player loginPlayer = CreateDrawCompatibilityPlayer(playerId);
            loginPlayer.PlayerData.Level = level;
            int savesBefore = transfinitePlayerCollection.ReplaceOneCalls;
            prepareTransfiniteLogin.Invoke(null, [loginPlayer, transfiniteLoginNow]);
            TransfiniteState prepared = loginPlayer.Transfinite
                ?? throw new InvalidDataException($"Transfinite level-{level} login omitted state.");
            AssertEqual(expectedRegion, prepared.RegionId, $"Transfinite level-{level} region");
            AssertEqual(
                transfiniteLoginNow - transfiniteLoginNow % TableReaderV2.Parse<TransfiniteActivityTable>()
                    .Single(activity => activity.Id == prepared.ActivityId).CycleSeconds,
                prepared.BeginTime,
                $"Transfinite level-{level} cycle begin");
            NotifyTransfiniteData loginWire = (NotifyTransfiniteData)Call(
                "TransfiniteModule", "BuildNotify", [typeof(Player)], loginPlayer);
            AssertEqual(true, loginWire.TransfiniteData is not null,
                $"Transfinite level-{level} Battle entry");
            NotifyTransfiniteData initialRoundTrip = MessagePackSerializer.Deserialize<NotifyTransfiniteData>(
                MessagePackSerializer.Serialize(loginWire));
            AssertEqual(0, initialRoundTrip.TransfiniteData!.BattleInfo.Count,
                $"Transfinite level-{level} initial BattleInfo array");
            AssertEqual(0, initialRoundTrip.TransfiniteData.BestSpendTime.Count,
                $"Transfinite level-{level} initial BestSpendTime array");
            string initialTransfiniteJson = MessagePackSerializer.ConvertToJson(
                MessagePackSerializer.Serialize(initialRoundTrip));
            AssertEqual(true, initialTransfiniteJson.Contains("\"BattleInfo\":[]", StringComparison.Ordinal),
                $"Transfinite level-{level} initial BattleInfo list key");
            AssertEqual(true, initialTransfiniteJson.Contains("\"BestSpendTime\":[]", StringComparison.Ordinal),
                $"Transfinite level-{level} initial BestSpendTime list key");

            TransfiniteStageGroupTable stageGroup = TableReaderV2.Parse<TransfiniteStageGroupTable>()
                .Single(row => row.StageGroupId == prepared.StageGroupId);
            int passedStageId = stageGroup.StageId[0];
            prepared.BattleInfo = new TransfiniteBattleState
            {
                StageGroupId = prepared.StageGroupId,
                StageProgressIndex = 1,
                StageInfo = [new TransfiniteStageState { StageId = passedStageId, IsWin = true, Score = 1 }],
                Result = new TransfiniteBattleResultState()
            };
            prepared.BestSpendTime = new Dictionary<int, int>
            {
                [prepared.StageGroupId] = prepared.StageGroupId
            };
            NotifyTransfiniteData progressedRoundTrip = MessagePackSerializer.Deserialize<NotifyTransfiniteData>(
                MessagePackSerializer.Serialize((NotifyTransfiniteData)Call(
                    "TransfiniteModule", "BuildNotify", [typeof(Player)], loginPlayer)));
            TransfiniteData progressedData = progressedRoundTrip.TransfiniteData
                ?? throw new InvalidDataException($"Transfinite level-{level} progressed state omitted.");
            TransfiniteBattleInfo progressedBattle = progressedData.BattleInfo.Single();
            AssertEqual(prepared.StageGroupId, progressedBattle.StageGroupId,
                $"Transfinite level-{level} progressed BattleInfo group");
            AssertEqual(passedStageId, progressedBattle.StageInfo.Single().StageId,
                $"Transfinite level-{level} progressed StageInfo state");
            AssertEqual(1, progressedBattle.StageInfo.Single().Score,
                $"Transfinite level-{level} progressed StageInfo score");
            AssertEqual(prepared.StageGroupId, progressedData.BestSpendTime.Single().StageGroupId,
                $"Transfinite level-{level} progressed BestSpendTime group");
            AssertEqual(prepared.StageGroupId, progressedData.BestSpendTime.Single().BestSpendTime,
                $"Transfinite level-{level} progressed BestSpendTime state");
            AssertEqual(null, progressedBattle.TeamInfo,
                $"Transfinite level-{level} progressed TeamInfo safe null");
            AssertEqual(0, progressedBattle.Result!.CharacterResultList.Count,
                $"Transfinite level-{level} progressed Result safe empty");
            string progressedTransfiniteJson = MessagePackSerializer.ConvertToJson(
                MessagePackSerializer.Serialize(progressedRoundTrip));
            AssertEqual(true, progressedTransfiniteJson.Contains("\"BattleInfo\":[{\"StageGroupId\"", StringComparison.Ordinal),
                $"Transfinite level-{level} progressed BattleInfo object key");
            AssertEqual(true, progressedTransfiniteJson.Contains("\"BestSpendTime\":[{\"StageGroupId\"", StringComparison.Ordinal),
                $"Transfinite level-{level} progressed BestSpendTime object key");
            AssertEqual(savesBefore + 1, transfinitePlayerCollection.ReplaceOneCalls,
                $"Transfinite level-{level} initial persistence");

            prepareTransfiniteLogin.Invoke(null, [loginPlayer, transfiniteLoginNow]);
            AssertEqual(savesBefore + 1, transfinitePlayerCollection.ReplaceOneCalls,
                $"Transfinite level-{level} idempotent login");

            prepared.RegionId = int.MaxValue;
            prepareTransfiniteLogin.Invoke(null, [loginPlayer, transfiniteLoginNow]);
            AssertEqual(expectedRegion, loginPlayer.Transfinite!.RegionId,
                $"Transfinite level-{level} stale state repair");
            AssertEqual(savesBefore + 2, transfinitePlayerCollection.ReplaceOneCalls,
                $"Transfinite level-{level} repair persistence");
        }
        long settlePlayerId = 46_110;
        Player settlePlayer = CreateDrawCompatibilityPlayer(settlePlayerId);
        settlePlayer.Transfinite = new TransfiniteState
        {
            ActivityId = settleActivity.Id,
            ActivityAuthorizedUntil = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            RegionId = rewardCandidate.Group.RegionId,
            ScoreRewardGroupId = rewardCandidate.Group.ScoreRewardGroupId
                ?? throw new InvalidDataException("Transfinite reward group omitted ScoreRewardGroupId."),
            RotateSettleInfo = new TransfiniteRotateSettleState
            {
                RotationId = 1,
                RegionId = rewardCandidate.Group.RegionId,
                ScoreRewardGroupId = rewardCandidate.Group.ScoreRewardGroupId
                    ?? throw new InvalidDataException("Transfinite reward group omitted ScoreRewardGroupId."),
                MaxStageProgressIndex = 1,
                SettleTransfiniteScore = rewardCandidate.Group.Score[rewardCandidate.Index],
                GotScoreRewardIndex = [rewardCandidate.Index + 1]
            }
        };
        TransfiniteState pendingSettleTemplate =
            BsonSerializer.Deserialize<TransfiniteState>(settlePlayer.Transfinite.ToBson());

        (TransfiniteGetRotateSettleInfoResponse Response, NotifyItemDataList? ItemPush) InvokeSettleReceipt(
            LoopbackSessionHarness harness,
            int packetId,
            string assertionName)
        {
            InvokeRegisteredRequestHandler(
                nameof(TransfiniteGetRotateSettleInfoRequest),
                harness.Session,
                packetId,
                new TransfiniteGetRotateSettleInfoRequest());
            Packet packet = harness.ReadPacket($"{assertionName} first packet");
            NotifyItemDataList? itemPush = null;
            if (packet.Type == Packet.ContentType.Push)
            {
                Packet.Push pushEnvelope = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                AssertEqual(nameof(NotifyItemDataList), pushEnvelope.Name, $"{assertionName} push name");
                itemPush = MessagePackSerializer.Deserialize<NotifyItemDataList>(pushEnvelope.Content);
                packet = harness.ReadPacket($"{assertionName} response");
            }

            AssertEqual(Packet.ContentType.Response, packet.Type, $"{assertionName} response packet type");
            Packet.Response responseEnvelope = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
            AssertEqual(nameof(TransfiniteGetRotateSettleInfoResponse), responseEnvelope.Name,
                $"{assertionName} response packet name");
            return (
                MessagePackSerializer.Deserialize<TransfiniteGetRotateSettleInfoResponse>(responseEnvelope.Content),
                itemPush);
        }

        using (LoopbackSessionHarness settleHarness = new(
            CreateDrawCompatibilityCharacter(settlePlayerId),
            settlePlayer,
            CreateDrawCompatibilityInventory(settlePlayerId, []),
            "transfinite-reward-push"))
        {
            const int settlePacketId = 46_510;
            InvokeRegisteredRequestHandler(
                nameof(TransfiniteGetRotateSettleInfoRequest),
                settleHarness.Session,
                settlePacketId,
                new TransfiniteGetRotateSettleInfoRequest());

            Packet itemPushPacket = settleHarness.ReadPacket("Transfinite settlement item push");
            AssertEqual(Packet.ContentType.Push, itemPushPacket.Type, "Transfinite settlement first packet type");
            Packet.Push itemPush = MessagePackSerializer.Deserialize<Packet.Push>(itemPushPacket.Content);
            AssertEqual(nameof(NotifyItemDataList), itemPush.Name, "Transfinite settlement first packet name");
            NotifyItemDataList itemData = MessagePackSerializer.Deserialize<NotifyItemDataList>(itemPush.Content);
            AssertEqual(rewardCandidate.Goods.Count, itemData.ItemDataList.Count, "Transfinite settlement pushed item count");

            Packet settleResponsePacket = settleHarness.ReadPacket("Transfinite settlement response");
            AssertEqual(Packet.ContentType.Response, settleResponsePacket.Type, "Transfinite settlement second packet type");
            Packet.Response responsePacket = MessagePackSerializer.Deserialize<Packet.Response>(settleResponsePacket.Content);
            AssertEqual(nameof(TransfiniteGetRotateSettleInfoResponse), responsePacket.Name,
                "Transfinite settlement second packet name");
            TransfiniteGetRotateSettleInfoResponse response =
                MessagePackSerializer.Deserialize<TransfiniteGetRotateSettleInfoResponse>(responsePacket.Content);
            AssertEqual(0, response.Code, "Transfinite settlement response code");
            AssertEqual(rewardCandidate.Goods.Count, response.RewardGoodsList.Count,
                "Transfinite settlement response reward count");
            AssertEqual(null, settlePlayer.Transfinite.RotateSettleInfo,
                "Transfinite settlement consumes pending state after reward persistence");

            const int replayPacketId = 46_511;
            InvokeRegisteredRequestHandler(
                nameof(TransfiniteGetRotateSettleInfoRequest),
                settleHarness.Session,
                replayPacketId,
                new TransfiniteGetRotateSettleInfoRequest());
            Packet replayPacket = settleHarness.ReadPacket("Transfinite settlement replay response");
            AssertEqual(Packet.ContentType.Response, replayPacket.Type,
                "Transfinite settlement replay emits no duplicate mutation push");
            Packet.Response replayEnvelope = MessagePackSerializer.Deserialize<Packet.Response>(replayPacket.Content);
            TransfiniteGetRotateSettleInfoResponse replay =
                MessagePackSerializer.Deserialize<TransfiniteGetRotateSettleInfoResponse>(replayEnvelope.Content);
            AssertEqual(0, replay.Code, "Transfinite settlement replay response code");
            AssertEqual(response.RewardGoodsList.Count, replay.RewardGoodsList.Count,
                "Transfinite settlement replay returns persisted receipt");
        }

        const int transfiniteConfigNotFound = 20008002;
        long failurePlayerId = 46_111;
        Player inventoryFailurePlayer = CreateDrawCompatibilityPlayer(failurePlayerId);
        inventoryFailurePlayer.Transfinite =
            BsonSerializer.Deserialize<TransfiniteState>(pendingSettleTemplate.ToBson());
        Inventory inventoryFailureInventory = CreateDrawCompatibilityInventory(failurePlayerId, []);
        using (LoopbackSessionHarness failureHarness = new(
            CreateDrawCompatibilityCharacter(failurePlayerId),
            inventoryFailurePlayer,
            inventoryFailureInventory,
            "transfinite-inventory-save-failure"))
        {
            int inventorySaveCallsBefore = transfiniteInventoryCollection.ReplaceOneCalls;
            int playerSaveCallsBefore = transfinitePlayerCollection.ReplaceOneCalls;
            transfiniteInventoryCollection.ThrowOnReplaceOne = true;
            var failed = InvokeSettleReceipt(failureHarness, 46_512, "Transfinite inventory save failure");
            transfiniteInventoryCollection.ThrowOnReplaceOne = false;
            AssertEqual(transfiniteConfigNotFound, failed.Response.Code,
                "Transfinite inventory save failure response");
            AssertEqual(null, failed.ItemPush, "Transfinite inventory save failure emits no reward push");
            AssertEqual(0L, inventoryFailureInventory.Items
                .SingleOrDefault(item => item.Id == rewardCandidate.Goods[0].TemplateId)?.Count ?? 0,
                "Transfinite inventory save failure restores in-memory items");
            AssertEqual(0, inventoryFailureInventory.TransfiniteReceipts.Count,
                "Transfinite inventory save failure restores reward ledger");
            AssertEqual(true, inventoryFailurePlayer.Transfinite!.RotateSettleInfo is not null,
                "Transfinite inventory save failure preserves pending settlement");
            AssertEqual(inventorySaveCallsBefore + 1, transfiniteInventoryCollection.ReplaceOneCalls,
                "Transfinite inventory save failure attempted one inventory write");
            AssertEqual(playerSaveCallsBefore, transfinitePlayerCollection.ReplaceOneCalls,
                "Transfinite inventory save failure does not write player state");

            var retry = InvokeSettleReceipt(failureHarness, 46_513, "Transfinite inventory save retry");
            AssertEqual(0, retry.Response.Code, "Transfinite inventory save retry response");
            AssertEqual(true, retry.ItemPush is not null, "Transfinite inventory save retry emits reward push");
            AssertEqual((long)rewardCandidate.Goods[0].Count, inventoryFailureInventory.Items
                .Single(item => item.Id == rewardCandidate.Goods[0].TemplateId).Count,
                "Transfinite inventory save retry grants reward once");
            AssertEqual(1, inventoryFailureInventory.TransfiniteReceipts.Count,
                "Transfinite inventory save retry persists one ledger entry");
            AssertEqual(null, inventoryFailurePlayer.Transfinite.RotateSettleInfo,
                "Transfinite inventory save retry consumes pending settlement");
        }

        failurePlayerId = 46_112;
        Player playerFailurePlayer = CreateDrawCompatibilityPlayer(failurePlayerId);
        playerFailurePlayer.Transfinite =
            BsonSerializer.Deserialize<TransfiniteState>(pendingSettleTemplate.ToBson());
        Inventory playerFailureInventory = CreateDrawCompatibilityInventory(failurePlayerId, []);
        using (LoopbackSessionHarness failureHarness = new(
            CreateDrawCompatibilityCharacter(failurePlayerId),
            playerFailurePlayer,
            playerFailureInventory,
            "transfinite-player-save-failure"))
        {
            int inventorySaveCallsBefore = transfiniteInventoryCollection.ReplaceOneCalls;
            int playerSaveCallsBefore = transfinitePlayerCollection.ReplaceOneCalls;
            transfinitePlayerCollection.ThrowOnReplaceOne = true;
            var failed = InvokeSettleReceipt(failureHarness, 46_514, "Transfinite player save failure");
            transfinitePlayerCollection.ThrowOnReplaceOne = false;
            AssertEqual(transfiniteConfigNotFound, failed.Response.Code,
                "Transfinite player save failure response");
            AssertEqual(null, failed.ItemPush, "Transfinite player save failure emits no reward push");
            AssertEqual((long)rewardCandidate.Goods[0].Count, playerFailureInventory.Items
                .Single(item => item.Id == rewardCandidate.Goods[0].TemplateId).Count,
                "Transfinite player save failure retains persisted reward");
            AssertEqual(1, playerFailureInventory.TransfiniteReceipts.Count,
                "Transfinite player save failure retains persisted ledger");
            AssertEqual(true, playerFailurePlayer.Transfinite!.RotateSettleInfo is not null,
                "Transfinite player save failure preserves pending settlement");
            AssertEqual(inventorySaveCallsBefore + 1, transfiniteInventoryCollection.ReplaceOneCalls,
                "Transfinite player save failure writes inventory once");
            AssertEqual(playerSaveCallsBefore + 1, transfinitePlayerCollection.ReplaceOneCalls,
                "Transfinite player save failure attempts one player write");

            var retry = InvokeSettleReceipt(failureHarness, 46_515, "Transfinite player save retry");
            AssertEqual(0, retry.Response.Code, "Transfinite player save retry response");
            AssertEqual(true, retry.ItemPush is not null, "Transfinite player save retry emits reward push");
            AssertEqual((long)rewardCandidate.Goods[0].Count, playerFailureInventory.Items
                .Single(item => item.Id == rewardCandidate.Goods[0].TemplateId).Count,
                "Transfinite player save retry does not duplicate reward");
            AssertEqual(1, playerFailureInventory.TransfiniteReceipts.Count,
                "Transfinite player save retry preserves one ledger entry");
            AssertEqual(inventorySaveCallsBefore + 1, transfiniteInventoryCollection.ReplaceOneCalls,
                "Transfinite player save retry does not rewrite inventory");
            AssertEqual(null, playerFailurePlayer.Transfinite.RotateSettleInfo,
                "Transfinite player save retry consumes pending settlement");

            var replay = InvokeSettleReceipt(failureHarness, 46_516, "Transfinite committed replay");
            AssertEqual(0, replay.Response.Code, "Transfinite committed replay response");
            AssertEqual(null, replay.ItemPush, "Transfinite committed replay emits no mutation push");
            AssertEqual((long)rewardCandidate.Goods[0].Count, playerFailureInventory.Items
                .Single(item => item.Id == rewardCandidate.Goods[0].TemplateId).Count,
                "Transfinite committed replay does not duplicate reward");
            AssertEqual(1, playerFailureInventory.TransfiniteReceipts.Count,
                "Transfinite committed replay preserves one ledger entry");
        }

        List<TransfiniteStageTable> transfiniteStages = TableReaderV2.Parse<TransfiniteStageTable>()
            .GroupBy(row => row.StageId)
            .Select(group => group.First())
            .Take(2)
            .ToList();
        if (transfiniteStages.Count < 2)
            throw new InvalidDataException("Transfinite fight gate requires two table-derived stages.");

        for (int index = 0; index < transfiniteStages.Count; index++)
        {
            TransfiniteStageTable stage = transfiniteStages[index];
            long playerId = 46_120 + index;
            Player player = CreateDrawCompatibilityPlayer(playerId);
            if (index == 1)
            {
                player.Transfinite = new TransfiniteState
                {
                    ActivityId = 1,
                    ActivityAuthorizedUntil = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                };
            }

            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                $"transfinite-fight-gate-{index}");
            int expectedCode = index == 0 ? 20008001 : 20008002;
            int preFightPacketId = 46_520 + index;
            InvokeRegisteredRequestHandler(
                nameof(PreFightRequest),
                harness.Session,
                preFightPacketId,
                new PreFightRequest
                {
                    PreFightData = new()
                    {
                        StageId = checked((uint)stage.StageId),
                        CardIds = [],
                        RobotIds = []
                    }
                });
            PreFightResponse preFightResponse = ReadResponsePayload<PreFightResponse>(
                harness,
                preFightPacketId,
                nameof(PreFightResponse),
                $"Transfinite table stage {stage.StageId} pre-fight gate");
            AssertEqual(expectedCode, preFightResponse.Code, "Transfinite fail-closed pre-fight code");

            int settlePacketId = 46_530 + index;
            InvokeRegisteredRequestHandler(
                nameof(FightSettleRequest),
                harness.Session,
                settlePacketId,
                new FightSettleRequest
                {
                    Result = new FightSettleResult
                    {
                        StageId = checked((uint)stage.StageId),
                        IsWin = true
                    }
                });
            FightSettleResponse settleResponse = ReadResponsePayload<FightSettleResponse>(
                harness,
                settlePacketId,
                nameof(FightSettleResponse),
                $"Transfinite table stage {stage.StageId} settle gate");
            AssertEqual(1033, settleResponse.Code, "Transfinite settle without authorized fight context");
            AssertEqual(0, harness.Session.stage?.Stages.Count ?? 0,
                "Transfinite rejected settle does not write generic stage progress");
        }
    }

    private static void ValidateTransfiniteCompatibility()
    {
        Type Payload(string name) => RequiredAscNetGameServerType($"AscNet.GameServer.Handlers.{name}");
        void AssertDto(string name, params string[] keys)
        {
            Type type = Payload(name);
            object value = Activator.CreateInstance(type)
                ?? throw new InvalidDataException($"{name} needs a public parameterless constructor.");
            byte[] bytes = MessagePackSerializer.Serialize(type, value);
            _ = MessagePackSerializer.Deserialize(type, bytes)
                ?? throw new InvalidDataException($"{name} did not round-trip.");
            AssertMailNamedMapKeys(type, value, keys, $"{name} wire keys");
        }

        // These are the current-client named-map contracts, not inferred runtime defaults.
        AssertDto("TransfiniteTeamInfo", "CharacterIdList", "CaptainPos", "FirstFightPos",
            "SelectedGeneralSkill", "EnterCgIndex", "SettleCgIndex");
        AssertDto("TransfiniteBattleResult", "LastWinStageId", "CharacterResultList", "StageSpendTime");
        AssertDto("TransfiniteStageInfo", "StageId", "IsWin", "SpendTime", "Score");
        AssertDto("TransfiniteBattleInfo", "StageGroupId", "StageProgressIndex", "StartStageProgress",
            "TeamInfo", "StageInfo", "Result", "LastResult", "HistoryResults");
        AssertDto("TransfiniteSetTeamRequest", "StageGroupId", "TeamInfo", "ResetStageIndex");
        AssertDto("TransfiniteSetTeamResponse", "Code", "BattleInfo");
        AssertDto("TransfiniteConfirmBattleResultRequest", "StageGroupId", "IsGiveUp");
        AssertDto("TransfiniteConfirmBattleResultResponse", "Code", "RewardGoodsList", "BattleInfo");
        AssertDto("TransfiniteResetStageGroupRequest", "StageGroupId");
        AssertDto("TransfiniteResetStageGroupResponse", "Code", "BattleInfo");
        AssertDto("TransfiniteGetScoreRewardRequest", "ScoreRewardIndex");
        AssertDto("TransfiniteGetScoreRewardResponse", "Code", "RewardGoodsList", "GotScoreRewardIndex");

        foreach (string request in new[]
        {
            "TransfiniteSetTeamRequest",
            "TransfiniteConfirmBattleResultRequest",
            "TransfiniteResetStageGroupRequest",
            "TransfiniteGetScoreRewardRequest",
            "TransfiniteGetRotateSettleInfoRequest"
        })
            _ = GetRegisteredRequestHandlerMethod(request);

        List<TransfiniteStageGroupTable> groups = TableReaderV2.Parse<TransfiniteStageGroupTable>()
            .Where(group => group.StageId.Count > 0)
            .GroupBy(group => string.Join(',', group.StageId))
            .Select(group => group.First())
            .Take(2)
            .ToList();
        AssertEqual(2, groups.Count, "Transfinite has two independently table-derived stage groups");
        AssertEqual(false, groups[0].StageGroupId == groups[1].StageGroupId,
            "Transfinite table-derived stage groups are distinct");
        AssertEqual(false, groups[0].StageId.SequenceEqual(groups[1].StageId),
            "Transfinite table-derived teams target distinct stage sequences");
        // Current source supplies no ResetStageGroup transition: it must remain explicitly fail-closed.
        long playerId = 46_180;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId), player, CreateDrawCompatibilityInventory(playerId, []),
            "transfinite-reset-boundary");
        string before = Convert.ToHexString(player.ToBson());
        Type resetRequestType = Payload("TransfiniteResetStageGroupRequest");
        object reset = Activator.CreateInstance(resetRequestType)
            ?? throw new InvalidDataException("TransfiniteResetStageGroupRequest cannot construct.");
        resetRequestType.GetProperty("StageGroupId")!.SetValue(reset, groups[0].StageGroupId);
        InvokeRegisteredRequestHandler("TransfiniteResetStageGroupRequest", harness.Session, 46_180, reset);
        Packet responsePacket = harness.ReadPacket("Transfinite reset response");
        Packet.Response envelope = MessagePackSerializer.Deserialize<Packet.Response>(responsePacket.Content);
        AssertEqual(before, Convert.ToHexString(player.ToBson()), "Transfinite ResetStageGroup is mutation-free");
        AssertEqual("TransfiniteResetStageGroupResponse", envelope.Name, "Transfinite reset response name");
        object response = MessagePackSerializer.Deserialize(Payload(envelope.Name), envelope.Content)
            ?? throw new InvalidDataException("Transfinite reset response nil.");
        AssertEqual(20008002, Convert.ToInt32(Payload(envelope.Name).GetProperty("Code")!.GetValue(response)),
            "Transfinite ResetStageGroup missing-source boundary");
        // Capture flow begins at progress 0 with the first stage one-based for display.
        TransfiniteActivityTable activity = TableReaderV2.Parse<TransfiniteActivityTable>()
            .First(row => ActivityScheduleService.TryGet(row.TimeId, out ActivityScheduleEntry schedule)
                && schedule.Source.StartsWith("version-history:", StringComparison.Ordinal));
        ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry activeSchedule);
        long flowPlayerId = 46_181;
        Player flowPlayer = CreateDrawCompatibilityPlayer(flowPlayerId);
        flowPlayer.PlayerData.Level = 80;
        Character flowCharacter = CreateDrawCompatibilityCharacter(flowPlayerId);
        flowCharacter.Characters = [new CharacterData { Id = 1 }, new CharacterData { Id = 2 }, new CharacterData { Id = 3 }];
        MethodInfo prepare = RequiredMethod(Payload("TransfiniteModule"), "PrepareLogin",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(Player), typeof(long)]);
        using MongoCollectionOverride flowMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> flowPlayerCollection,
            out RecordingMongoCollectionProxy<Character> flowCharacterCollection,
            out RecordingMongoCollectionProxy<Inventory> flowInventoryCollection);
        prepare.Invoke(null, [flowPlayer, activeSchedule.StartTime]);
        TransfiniteState flowState = flowPlayer.Transfinite
            ?? throw new InvalidDataException("Transfinite flow fixture was not authorized.");
        int sameCircleSaves = flowPlayerCollection.ReplaceOneCalls;
        prepare.Invoke(null, [flowPlayer, activeSchedule.StartTime]);
        AssertEqual(sameCircleSaves, flowPlayerCollection.ReplaceOneCalls, "Transfinite same-circle PrepareLogin does not save");
        long sentinelPlayerId = 46_183;
        Player sentinelPlayer = CreateDrawCompatibilityPlayer(sentinelPlayerId);
        sentinelPlayer.Transfinite = new TransfiniteState
        {
            ActivityId = activity.Id,
            ActivityAuthorizedUntil = long.MaxValue,
            RegionId = flowState.RegionId,
            ScoreRewardGroupId = flowState.ScoreRewardGroupId,
            RotateSettleInfo = new TransfiniteRotateSettleState
            {
                RotationId = 52,
                RegionId = flowState.RegionId,
                MaxStageProgressIndex = 6,
                ScoreRewardGroupId = 0
            }
        };
        using (LoopbackSessionHarness sentinelHarness = new(CreateDrawCompatibilityCharacter(sentinelPlayerId), sentinelPlayer,
                   CreateDrawCompatibilityInventory(sentinelPlayerId, []), "transfinite-group-zero-rotation"))
        {
            InvokeRegisteredRequestHandler(nameof(TransfiniteGetRotateSettleInfoRequest), sentinelHarness.Session, 46_179,
                new TransfiniteGetRotateSettleInfoRequest());
            TransfiniteGetRotateSettleInfoResponse sentinel = ReadResponsePayload<TransfiniteGetRotateSettleInfoResponse>(
                sentinelHarness, 46_179, nameof(TransfiniteGetRotateSettleInfoResponse), "Transfinite group-0 rotation receipt");
            AssertEqual(0, sentinel.Code, "Transfinite group-0 rotation receipt code");
            AssertEqual(6, sentinel.MaxStageProgressIndex, "Transfinite group-0 rotation receipt progress");
            AssertEqual(null, sentinelPlayer.Transfinite.RotateSettleInfo, "Transfinite group-0 rotation receipt consumes sentinel");
        }
        int flowGroup = flowState.StageGroupId;
        using LoopbackSessionHarness flowHarness = new(flowCharacter, flowPlayer,
            CreateDrawCompatibilityInventory(flowPlayerId, []), "transfinite-captured-team-flow");
        flowState.ActivityAuthorizedUntil = long.MaxValue;
        TransfiniteTeamInfo Team(params long[] ids) => new()
        {
            CharacterIdList = ids.ToList(), CaptainPos = 1, FirstFightPos = 1,
            SelectedGeneralSkill = 0
        };
        void AssertRejectedTeam(TransfiniteTeamInfo team, string name)
        {
            InvokeRegisteredRequestHandler(nameof(TransfiniteSetTeamRequest), flowHarness.Session, 46_181,
                new TransfiniteSetTeamRequest { StageGroupId = flowGroup, TeamInfo = team, ResetStageIndex = true });
            TransfiniteSetTeamResponse rejected = ReadResponsePayload<TransfiniteSetTeamResponse>(flowHarness, 46_181,
                nameof(TransfiniteSetTeamResponse), name);
            AssertEqual(20008002, rejected.Code, name);
        }
        TransfiniteTeamInfo invalidSkill = Team(1, 0, 0);
        invalidSkill.SelectedGeneralSkill = int.MaxValue;
        AssertRejectedTeam(invalidSkill, "Transfinite configured general skill boundary");
        AssertRejectedTeam(Team(1, 1, 0), "Transfinite duplicate team member");
        AssertRejectedTeam(Team(99, 0, 0), "Transfinite unowned team member");
        TransfiniteTeamInfo invalidPosition = Team(1, 0, 0);
        invalidPosition.CaptainPos = 2;
        AssertRejectedTeam(invalidPosition, "Transfinite captain selects nonzero slot");

        InvokeRegisteredRequestHandler(nameof(TransfiniteSetTeamRequest), flowHarness.Session, 46_182,
            new TransfiniteSetTeamRequest { StageGroupId = flowGroup, TeamInfo = Team(1, 2, 0), ResetStageIndex = true });
        TransfiniteSetTeamResponse setTeam = ReadResponsePayload<TransfiniteSetTeamResponse>(flowHarness, 46_182,
            nameof(TransfiniteSetTeamResponse), "Transfinite SetTeam response-only");
        AssertEqual(0, setTeam.Code, "Transfinite SetTeam success");
        TransfiniteBattleInfo teamBattle = setTeam.BattleInfo
            ?? throw new InvalidDataException("Transfinite SetTeam omitted BattleInfo.");
        AssertEqual(0, teamBattle.StageProgressIndex, "Transfinite captured SetTeam progress");
        AssertEqual(1, teamBattle.StartStageProgress, "Transfinite captured SetTeam start");
        AssertEqual(0, teamBattle.StageInfo.Count, "Transfinite SetTeam has no stage result");
        AssertEqual(true, teamBattle.TeamInfo is not null && teamBattle.Result is not null,
            "Transfinite TeamInfo always carries Result");
        int fightStageId = TableReaderV2.Parse<TransfiniteStageGroupTable>()
            .Single(group => group.StageGroupId == flowGroup).StageId[0];
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), flowHarness.Session, 46_183,
            new PreFightRequest { PreFightData = new() { StageId = (uint)fightStageId, CardIds = [], RobotIds = [] } });
        PreFightResponse preFight = ReadResponsePayload<PreFightResponse>(flowHarness, 46_183,
            nameof(PreFightResponse), "Transfinite authorized PreFight");
        AssertEqual(0, preFight.Code, "Transfinite authorized PreFight code");
        AssertEqual((uint)fightStageId, preFight.FightData.StageId, "Transfinite PreFight falls through with FightData");
        AssertEqual(true, flowHarness.Session.fight is not null, "Transfinite PreFight creates session fight");
        InvokeRegisteredRequestHandler(nameof(TransfiniteSetTeamRequest), flowHarness.Session, 46_283,
            new TransfiniteSetTeamRequest { StageGroupId = flowGroup, TeamInfo = Team(1, 2, 0), ResetStageIndex = true });
        AssertEqual(20008002, ReadResponsePayload<TransfiniteSetTeamResponse>(
            flowHarness, 46_283, nameof(TransfiniteSetTeamResponse), "Transfinite active-fight SetTeam").Code,
            "Transfinite active fight rejects team replacement");
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), flowHarness.Session, 46_184,
            new FightSettleRequest { Result = new FightSettleResult { StageId = (uint)fightStageId, FightId = preFight.FightData.FightId, IsWin = true } });
        FightSettleResponse settled = ReadResponsePayload<FightSettleResponse>(flowHarness, 46_184,
            nameof(FightSettleResponse), "Transfinite winning FightSettle");
        AssertEqual(0, settled.Code, "Transfinite winning FightSettle code");
        AssertEqual(true, flowPlayer.Transfinite!.BattleInfo!.LastResult is not null,
            "Transfinite FightSettle persists typed pending LastResult");
        AssertEqual(0, flowHarness.Session.stage?.Stages.Count ?? 0,
            "Transfinite never writes generic Stage progress");
        InvokeRegisteredRequestHandler(nameof(TransfiniteSetTeamRequest), flowHarness.Session, 46_284,
            new TransfiniteSetTeamRequest { StageGroupId = flowGroup, TeamInfo = Team(1, 2, 0), ResetStageIndex = true });
        AssertEqual(20008002, ReadResponsePayload<TransfiniteSetTeamResponse>(
            flowHarness, 46_284, nameof(TransfiniteSetTeamResponse), "Transfinite pending-result SetTeam").Code,
            "Transfinite pending result rejects team replacement");
        // A give-up only consumes a typed pending result; it does not turn it into progress.
        InvokeRegisteredRequestHandler(nameof(TransfiniteConfirmBattleResultRequest), flowHarness.Session, 46_185,
            new TransfiniteConfirmBattleResultRequest { StageGroupId = flowGroup, IsGiveUp = true });
        TransfiniteConfirmBattleResultResponse gaveUp = ReadResponsePayload<TransfiniteConfirmBattleResultResponse>(
            flowHarness, 46_185, nameof(TransfiniteConfirmBattleResultResponse), "Transfinite pending give-up");
        AssertEqual(0, gaveUp.Code, "Transfinite pending give-up code");
        AssertEqual(null, flowPlayer.Transfinite!.BattleInfo!.LastResult, "Transfinite give-up clears only pending result");
        AssertEqual(0, flowPlayer.Transfinite.BattleInfo.StageProgressIndex, "Transfinite give-up preserves progress");
        AssertEqual(0, flowPlayer.Transfinite.BattleInfo.StageInfo.Count, "Transfinite give-up preserves stage history");
        AssertEqual(0, flowPlayer.MissionProgress.ConditionCounters
            .Where(counter => TableReaderV2.Parse<TaskTable>().Any(task => task.Type == 79 && task.Condition == counter.Key))
            .Sum(counter => counter.Value), "Transfinite give-up does not advance challenge tasks");

        Dictionary<int, TransfiniteStageTable> stageRows = TableReaderV2.Parse<TransfiniteStageTable>()
            .ToDictionary(row => row.StageId);
        List<TransfiniteStageGroupTable> allStageGroups = TableReaderV2.Parse<TransfiniteStageGroupTable>().ToList();
        List<TransfiniteRotateGroupTable> allRotates = TableReaderV2.Parse<TransfiniteRotateGroupTable>().ToList();
        TransfiniteStageGroupTable fourteenStages = allStageGroups.First(group =>
            group.Type == 2
            && group.StageId.Count == 14
            && group.StageId.All(stageRows.ContainsKey)
            && group.StageId.Sum(id => stageRows[id].Score ?? 0) > 0
            && group.StageId.Any(id => stageRows[id].StageType == 2
                && stageRows[id].ExtraScore > 0 && stageRows[id].ExtraTimeLimit > 0));
        TransfiniteRotateGroupTable flowRotate = allRotates
            .First(rotate => rotate.StageGroupId.Contains(fourteenStages.StageGroupId));
        ItemTable scoreItem = TableReaderV2.Parse<ItemTable>().Single(item => item.Id == 105);
        TransfiniteRegionTable flowRegion = TableReaderV2.Parse<TransfiniteRegionTable>()
            .First(region => region.RotateGroupId == flowRotate.RotateGroupId);
        TransfiniteScoreRewardGroupTable flowRewards = TableReaderV2.Parse<TransfiniteScoreRewardGroupTable>()
            .Single(group => group.RegionId == flowRegion.RegionId
                && group.ScoreRewardGroupId == flowRegion.ScoreRewardGroupId);
        long fullPlayerId = 46_182;
        Player fullPlayer = CreateDrawCompatibilityPlayer(fullPlayerId);
        fullPlayer.Transfinite = new TransfiniteState
        {
            ActivityId = activity.Id,
            ActivityAuthorizedUntil = long.MaxValue,
            CircleId = 1,
            RegionId = flowRegion.RegionId,
            ScoreRewardGroupId = flowRegion.ScoreRewardGroupId,
            StageGroupId = fourteenStages.StageGroupId
        };
        Character fullCharacter = CreateDrawCompatibilityCharacter(fullPlayerId);
        fullCharacter.Characters = [new CharacterData { Id = 1 }, new CharacterData { Id = 2 }, new CharacterData { Id = 3 }];
        using LoopbackSessionHarness fullHarness = new(fullCharacter, fullPlayer,
            CreateDrawCompatibilityInventory(fullPlayerId, []), "transfinite-fourteen-stage-flow");
        fullHarness.Session.stage = CreateLoginAccountCompatibilityStage(fullPlayerId);
        InvokeRegisteredRequestHandler(nameof(TransfiniteSetTeamRequest), fullHarness.Session, 46_186,
            new TransfiniteSetTeamRequest { StageGroupId = fourteenStages.StageGroupId, TeamInfo = Team(1, 2, 3), ResetStageIndex = true });
        AssertEqual(0, ReadResponsePayload<TransfiniteSetTeamResponse>(fullHarness, 46_186,
            nameof(TransfiniteSetTeamResponse), "Transfinite fourteen-stage SetTeam").Code,
            "Transfinite fourteen-stage SetTeam code");
        HashSet<int> expectedAchievementTaskGroupIds = TableReaderV2.Parse<TransfiniteAchievementTable>()
            .Where(achievement => achievement.Type == fourteenStages.Type
                && achievement.StageGroupId.Contains(fourteenStages.StageGroupId))
            .Select(achievement => achievement.Id)
            .ToHashSet();
        HashSet<int> activeAchievementTaskIds = TableReaderV2.Parse<TransfiniteTaskGroupTable>()
            .Where(group => expectedAchievementTaskGroupIds.Contains(group.Id))
            .SelectMany(group => group.TaskIds)
            .ToHashSet();
        HashSet<int> ExpectedAchievementTaskIds(int stageId) => TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>()
            .Where(task => activeAchievementTaskIds.Contains(task.Id))
            .Join(TableReaderV2.Parse<ConditionTable>(), task => task.Condition, condition => condition.Id,
                (task, condition) => (task, condition))
            .Where(pair => pair.condition.Type == 103000
                && pair.condition.Params.Skip(1).Contains(stageId))
            .Select(pair => pair.task.Id)
            .ToHashSet();


        int expectedScore = 0;
        int expectedSpend = 0;
        for (int index = 0; index < fourteenStages.StageId.Count; index++)
        {
            int stageId = fourteenStages.StageId[index];
            TransfiniteStageTable row = stageRows[stageId];
            int spend = row.StageType == 2 ? row.ExtraTimeLimit!.Value - 1 : index + 1;
            int score = (row.Score ?? 0) + (row.StageType == 2 ? row.ExtraScore ?? 0 : 0);
            expectedScore += score;
            expectedSpend += spend;
            int packetId = 46_200 + index * 3;
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), fullHarness.Session, packetId,
                new PreFightRequest { PreFightData = new() { StageId = (uint)stageId, CardIds = [], RobotIds = [] } });
            PreFightResponse started = ReadResponsePayload<PreFightResponse>(fullHarness, packetId,
                nameof(PreFightResponse), $"Transfinite stage {index + 1} PreFight");
            AssertEqual(0, started.Code, $"Transfinite stage {index + 1} PreFight code");
            Dictionary<int, NpcHp> hp = index == 0
                ? new()
                {
                    [999] = new NpcHp
                    {
                        CharacterId = 1, Type = 1, BuffIds = [],
                        AttrTable = new()
                        {
                            [1] = new Dictionary<object, object> { ["Value"] = 55, ["MaxValue"] = 100 },
                            [2] = new Dictionary<object, object> { ["Value"] = 37 }
                        }
                    }
                }
                : new();
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), fullHarness.Session, packetId + 1,
                new FightSettleRequest { Result = new FightSettleResult
                {
                    StageId = (uint)stageId, FightId = started.FightData.FightId, IsWin = true,
                    LeftTime = spend, NpcHpInfo = hp
                } });
            AssertEqual(0, ReadResponsePayload<FightSettleResponse>(fullHarness, packetId + 1,
                nameof(FightSettleResponse), $"Transfinite stage {index + 1} FightSettle").Code,
                $"Transfinite stage {index + 1} FightSettle code");
            TransfiniteBattleState pending = fullPlayer.Transfinite!.BattleInfo!;
            AssertEqual(stageId, pending.LastResult!.LastWinStageId, $"Transfinite stage {index + 1} pending stage");
            if (index == 0)
            {
                AssertEqual(55, pending.LastResult.CharacterResultList.Single(x => x.CharacterId == 1).HpPercent,
                    "Transfinite NpcHp values map independently of dictionary slot");
                AssertEqual(37, pending.LastResult.CharacterResultList.Single(x => x.CharacterId == 1).Energy,
                    "Transfinite NpcHp energy maps independently of dictionary slot");
                AssertEqual(stageId, BsonSerializer.Deserialize<Player>(fullPlayer.ToBson()).Transfinite!.BattleInfo!.LastResult!.LastWinStageId,
                    "Transfinite pending BSON reconnect fidelity");
            }
            InvokeRegisteredRequestHandler(nameof(TransfiniteConfirmBattleResultRequest), fullHarness.Session, packetId + 2,
                new TransfiniteConfirmBattleResultRequest { StageGroupId = fourteenStages.StageGroupId });
            HashSet<int> expectedAchievementTaskIds = ExpectedAchievementTaskIds(stageId);
            TransfiniteConfirmBattleResultResponse? confirmed = null;
            if (index + 1 < fourteenStages.StageId.Count)
            {
                bool sawAchievement = expectedAchievementTaskIds.Count == 0;
                while (true)
                {
                    Packet next = fullHarness.ReadPacket($"Transfinite stage {index + 1} confirmation");
                    if (next.Type == Packet.ContentType.Response)
                    {
                        Packet.Response confirmationEnvelope = MessagePackSerializer.Deserialize<Packet.Response>(next.Content);
                        AssertEqual(nameof(TransfiniteConfirmBattleResultResponse), confirmationEnvelope.Name,
                            $"Transfinite stage {index + 1} Confirm response name");
                        confirmed = MessagePackSerializer.Deserialize<TransfiniteConfirmBattleResultResponse>(confirmationEnvelope.Content);
                        break;
                    }
                    AssertEqual(Packet.ContentType.Push, next.Type, $"Transfinite stage {index + 1} task push precedes response");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(next.Content);
                    AssertEqual(nameof(NotifyTask), push.Name, $"Transfinite stage {index + 1} task push name");
                    NotifyTask task = MessagePackSerializer.Deserialize<NotifyTask>(push.Content);
                    sawAchievement |= string.Join(',', expectedAchievementTaskIds.Order()) == string.Join(',', task.Tasks.Tasks.Select(entry => entry.Id).Order());
                }
                AssertEqual(true, sawAchievement, $"Transfinite stage {index + 1} table-derived achievement task push");
            }

            if (index + 1 < fourteenStages.StageId.Count)
            {
                AssertEqual(0, confirmed!.Code, $"Transfinite stage {index + 1} Confirm code");
                AssertEqual(score, fullPlayer.Transfinite!.BattleInfo!.StageInfo[index].Score,
                    $"Transfinite stage {index + 1} table score");
                if (index == 0)
                {
                    AssertEqual(1, BsonSerializer.Deserialize<Player>(fullPlayer.ToBson()).Transfinite!.BattleInfo!.StageProgressIndex,
                        "Transfinite confirmed BSON reconnect fidelity");
                    TaskTable timedTask = TableReaderV2.Parse<TaskTable>().First(task => task.Type == 79 && task.Result == 1);
                    AssertEqual(0, fullPlayer.MissionProgress.ConditionCounters.GetValueOrDefault(timedTask.Condition),
                        "Transfinite non-time-limit fight does not advance time-limit challenge");
                }
            }
            else
            {
                Packet terminalPush;
                while (true)
                {
                    terminalPush = fullHarness.ReadPacket("Transfinite terminal push");
                    if (terminalPush.Type != Packet.ContentType.Push
                        || MessagePackSerializer.Deserialize<Packet.Push>(terminalPush.Content).Name != nameof(NotifyTask))
                        break;
                }
                if (terminalPush.Type == Packet.ContentType.Response)
                {
                    TransfiniteConfirmBattleResultResponse early = MessagePackSerializer.Deserialize<TransfiniteConfirmBattleResultResponse>(
                        MessagePackSerializer.Deserialize<Packet.Response>(terminalPush.Content).Content);
                    throw new InvalidDataException(
                        $"Transfinite terminal responded before item push: Code={early.Code}, rewards={early.RewardGoodsList?.Count ?? 0}.");
                }
                AssertEqual(Packet.ContentType.Push, terminalPush.Type, "Transfinite terminal push precedes response");
                AssertEqual(nameof(NotifyItemDataList), MessagePackSerializer.Deserialize<Packet.Push>(terminalPush.Content).Name,
                    "Transfinite terminal score push name");
                NotifyItemDataList terminalItems = MessagePackSerializer.Deserialize<NotifyItemDataList>(
                    MessagePackSerializer.Deserialize<Packet.Push>(terminalPush.Content).Content);
                AssertEqual(Math.Min((long)expectedScore, Inventory.GetMaxCount(scoreItem)),
                    terminalItems.ItemDataList.Single(item => item.Id == 105).Count,
                    "Transfinite terminal item push applies Item table cap");
                TransfiniteConfirmBattleResultResponse terminal = ReadResponsePayload<TransfiniteConfirmBattleResultResponse>(
                    fullHarness, packetId + 2, nameof(TransfiniteConfirmBattleResultResponse), "Transfinite terminal Confirm");
                AssertEqual(0, terminal.Code, "Transfinite terminal Confirm code");
                AssertEqual(expectedScore, terminal.RewardGoodsList!.Single(x => x.TemplateId == 105).Count,
                    "Transfinite terminal grants raw summed score currency");
            }
        }
        AssertEqual(null, fullPlayer.Transfinite!.BattleInfo, "Transfinite terminal clears BattleInfo");
        AssertEqual(expectedSpend, fullPlayer.Transfinite.BestSpendTime[fourteenStages.StageGroupId],
            "Transfinite terminal records total BestSpendTime");
        AssertEqual(Math.Min((long)expectedScore, Inventory.GetMaxCount(scoreItem)),
            fullHarness.Session.inventory.Items.Single(x => x.Id == 105).Count,
            "Transfinite terminal score inventory applies Item table cap");
        AssertEqual(0, fullHarness.Session.stage?.Stages.Count ?? 0, "Transfinite fourteen-stage flow never writes generic stages");
        HashSet<int> activeChallengeTaskIds = TableReaderV2.Parse<TransfiniteTaskGroupTable>()
            .Single(group => group.Id == flowRegion.TaskGroupId).TaskIds.ToHashSet();
        List<TaskTable> challengeTasks = TableReaderV2.Parse<TaskTable>()
            .Where(task => activeChallengeTaskIds.Contains(task.Id))
            .ToList();
        foreach (int target in challengeTasks.Select(task => task.Result ?? 1).Distinct().Where(target => target <= fourteenStages.StageId.Count))
        {
            TaskTable task = challengeTasks.First(task => task.Result == target);
            AssertEqual(target, fullPlayer.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition),
                $"Transfinite challenge target {target} persists confirmed progress");
            AssertEqual(target, BsonSerializer.Deserialize<Player>(fullPlayer.ToBson()).MissionProgress
                    .ConditionCounters.GetValueOrDefault(task.Condition),
                $"Transfinite challenge target {target} reloads progress");
        }
        FinishMultiTaskResponse ReadFinishMultiTask(string name, bool hasReward)
        {
            Packet taskPacket = fullHarness.ReadPacket($"{name} task push");
            AssertEqual(Packet.ContentType.Push, taskPacket.Type, $"{name} task packet type");
            AssertEqual(nameof(NotifyTask), MessagePackSerializer.Deserialize<Packet.Push>(taskPacket.Content).Name,
                $"{name} task push name");
            if (hasReward)
            {
                Packet rewardPacket = fullHarness.ReadPacket($"{name} item push");
                AssertEqual(Packet.ContentType.Push, rewardPacket.Type, $"{name} item packet type");
                AssertEqual(nameof(NotifyItemDataList), MessagePackSerializer.Deserialize<Packet.Push>(rewardPacket.Content).Name,
                    $"{name} item push name");
            }
            return ReadResponsePayload<FinishMultiTaskResponse>(
                fullHarness, 46_253, nameof(FinishMultiTaskResponse), name);
        }

        TaskTable rewardTask = challengeTasks.OrderBy(task => task.Result).First();
        InvokeRegisteredRequestHandler(nameof(FinishMultiTaskRequest), fullHarness.Session, 46_253,
            new FinishMultiTaskRequest { TaskIds = [rewardTask.Id] });
        FinishMultiTaskResponse challengeClaim = ReadFinishMultiTask("Transfinite challenge claim", hasReward: true);
        AssertEqual(0, challengeClaim.Code, "Transfinite challenge claim code");
        AssertEqual(rewardTask.Id, challengeClaim.SuccessTaskIds.Single(), "Transfinite challenge claims requested task");
        AssertEqual(true, challengeClaim.RewardGoodsList.Count > 0, "Transfinite challenge claim returns table reward");
        InvokeRegisteredRequestHandler(nameof(FinishMultiTaskRequest), fullHarness.Session, 46_253,
            new FinishMultiTaskRequest { TaskIds = [rewardTask.Id] });
        FinishMultiTaskResponse challengeReplay = ReadFinishMultiTask("Transfinite challenge claim replay", hasReward: false);
        AssertEqual(0, challengeReplay.Code, "Transfinite challenge replay protocol code");
        AssertEqual(0, challengeReplay.SuccessTaskIds.Count, "Transfinite challenge replay grants nothing");
        AssertEqual(rewardTask.Id, challengeReplay.NotDealTaskIds.Single(), "Transfinite challenge replay is rejected");
        TransfiniteIslandTable spillIsland = TableReaderV2.Parse<TransfiniteIslandTable>()
            .Single(island => island.Id == flowRegion.IslandId);
        int spillGroupId = spillIsland.StageGroupId[0];
        int spillStageId = allStageGroups.Single(group => group.StageGroupId == spillGroupId).StageId[0];
        InvokeRegisteredRequestHandler(nameof(TransfiniteSetTeamRequest), fullHarness.Session, 46_254,
            new TransfiniteSetTeamRequest { StageGroupId = spillGroupId, TeamInfo = Team(1, 2, 3), ResetStageIndex = true });
        TransfiniteSetTeamResponse spillTeam = ReadResponsePayload<TransfiniteSetTeamResponse>(
            fullHarness, 46_254, nameof(TransfiniteSetTeamResponse), "Transfinite Spill Point SetTeam");
        AssertEqual(0, spillTeam.Code, "Transfinite Spill Point table group starts after terminal");
        AssertEqual(spillGroupId, spillTeam.BattleInfo!.StageGroupId, "Transfinite Spill Point keeps selected group");
        TransfiniteState spillReload = BsonSerializer.Deserialize<Player>(fullPlayer.ToBson()).Transfinite!;
        AssertEqual(spillGroupId, spillReload.BattleInfo!.StageGroupId, "Transfinite Spill Point relog group");
        AssertEqual(true, spillReload.BattleInfo.Result is not null, "Transfinite Spill Point relog safe Result");
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), fullHarness.Session, 46_255,
            new PreFightRequest { PreFightData = new() { StageId = (uint)spillStageId, CardIds = [], RobotIds = [] } });
        PreFightResponse spillPreFight = ReadResponsePayload<PreFightResponse>(
            fullHarness, 46_255, nameof(PreFightResponse), "Transfinite Spill Point PreFight");
        AssertEqual(0, spillPreFight.Code, "Transfinite Spill Point PreFight code");
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), fullHarness.Session, 46_256,
            new FightSettleRequest { Result = new FightSettleResult
            {
                StageId = (uint)spillStageId, FightId = spillPreFight.FightData.FightId, IsWin = true
            } });
        AssertEqual(0, ReadResponsePayload<FightSettleResponse>(
            fullHarness, 46_256, nameof(FightSettleResponse), "Transfinite Spill Point FightSettle").Code,
            "Transfinite Spill Point FightSettle code");
        AssertEqual(spillStageId, BsonSerializer.Deserialize<Player>(fullPlayer.ToBson()).Transfinite!
            .BattleInfo!.LastResult!.LastWinStageId, "Transfinite Spill Point pending relog result");
        int retryProgress = fullPlayer.Transfinite!.BattleInfo!.StageProgressIndex;
        int retryStageCount = fullPlayer.Transfinite.BattleInfo.StageInfo.Count;
        int retryHistoryCount = fullPlayer.Transfinite.BattleInfo.HistoryResults.Count;
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), fullHarness.Session, 46_257,
            new PreFightRequest { PreFightData = new() { StageId = (uint)spillStageId, CardIds = [], RobotIds = [] } });
        PreFightResponse retryPreFight = ReadResponsePayload<PreFightResponse>(
            fullHarness, 46_257, nameof(PreFightResponse), "Transfinite Spill Point rechallenge PreFight");
        AssertEqual(0, retryPreFight.Code, "Transfinite same-stage pending result allows rechallenge");
        AssertEqual(true, fullHarness.Session.fight is not null, "Transfinite rechallenge creates session fight");
        AssertEqual(null, fullPlayer.Transfinite.BattleInfo.LastResult, "Transfinite rechallenge consumes pending result");
        AssertEqual(retryProgress, fullPlayer.Transfinite.BattleInfo.StageProgressIndex,
            "Transfinite rechallenge preserves stage progress");
        AssertEqual(retryStageCount, fullPlayer.Transfinite.BattleInfo.StageInfo.Count,
            "Transfinite rechallenge preserves stage history");
        AssertEqual(retryHistoryCount, fullPlayer.Transfinite.BattleInfo.HistoryResults.Count,
            "Transfinite rechallenge preserves result history");
        AssertEqual(null, BsonSerializer.Deserialize<Player>(fullPlayer.ToBson()).Transfinite!
            .BattleInfo!.LastResult, "Transfinite rechallenge persists consumed result");
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), fullHarness.Session, 46_357,
            new FightSettleRequest { Result = new FightSettleResult
            {
                StageId = (uint)spillStageId, FightId = retryPreFight.FightData.FightId, IsWin = true
            } });
        AssertEqual(0, ReadResponsePayload<FightSettleResponse>(
            fullHarness, 46_357, nameof(FightSettleResponse), "Transfinite Spill Point rechallenge FightSettle").Code,
            "Transfinite rechallenge can settle again");
        TransfiniteRegionTable otherRegion = TableReaderV2.Parse<TransfiniteRegionTable>()
            .Single(region => region.RegionId != flowRegion.RegionId);
        TransfiniteIslandTable otherIsland = TableReaderV2.Parse<TransfiniteIslandTable>()
            .Single(island => island.Id == otherRegion.IslandId);
        AssertEqual(false, otherIsland.StageGroupId.Contains(spillGroupId),
            "Transfinite Spill Point stale-group fixture");
        TransfiniteBattleResultState pendingSpillResult = fullPlayer.Transfinite!.BattleInfo!.LastResult!;
        fullPlayer.Transfinite.BattleInfo.LastResult = null;
        fullPlayer.Transfinite.RegionId = otherRegion.RegionId;
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), fullHarness.Session, 46_258,
            new PreFightRequest { PreFightData = new() { StageId = (uint)spillStageId, CardIds = [], RobotIds = [] } });
        AssertEqual(20008002, ReadResponsePayload<PreFightResponse>(
            fullHarness, 46_258, nameof(PreFightResponse), "Transfinite stale-region Spill Point PreFight").Code,
            "Transfinite stale-region Spill Point is rejected");
        fullPlayer.Transfinite.RegionId = flowRegion.RegionId;
        fullPlayer.Transfinite.BattleInfo.LastResult = pendingSpillResult;
        List<int> earnedRewardIndices = Enumerable.Range(0, Math.Min(flowRewards.Score.Count, flowRewards.RewardId.Count))
            .Where(index => flowRewards.Score[index] <= expectedScore && flowRewards.RewardId[index] > 0)
            .Take(3).ToList();
        AssertEqual(3, earnedRewardIndices.Count, "Transfinite terminal score earns three table rewards");
        List<int> nonmonotonicClaims = [earnedRewardIndices[0], earnedRewardIndices[2], earnedRewardIndices[1]];
        InvokeRegisteredRequestHandler(nameof(TransfiniteGetScoreRewardRequest), fullHarness.Session, 46_250,
            new TransfiniteGetScoreRewardRequest { ScoreRewardIndex = nonmonotonicClaims });
        Packet scorePush = fullHarness.ReadPacket("Transfinite score reward item push");
        AssertEqual(Packet.ContentType.Push, scorePush.Type, "Transfinite score claim push precedes response");
        AssertEqual(nameof(NotifyItemDataList), MessagePackSerializer.Deserialize<Packet.Push>(scorePush.Content).Name,
            "Transfinite score claim push name");
        TransfiniteGetScoreRewardResponse scoreClaim = ReadResponsePayload<TransfiniteGetScoreRewardResponse>(
            fullHarness, 46_250, nameof(TransfiniteGetScoreRewardResponse), "Transfinite nonmonotonic score claim");
        AssertEqual(0, scoreClaim.Code, "Transfinite nonmonotonic score claim code");
        AssertIntegerList(nonmonotonicClaims.Select(Convert.ToInt64).ToArray(),
            scoreClaim.GotScoreRewardIndex.Select(Convert.ToInt64).ToArray(),
            "Transfinite nonmonotonic score claim receipt order");
        InvokeRegisteredRequestHandler(nameof(TransfiniteGetScoreRewardRequest), fullHarness.Session, 46_251,
            new TransfiniteGetScoreRewardRequest { ScoreRewardIndex = [earnedRewardIndices[0]] });
        AssertEqual(20008002, ReadResponsePayload<TransfiniteGetScoreRewardResponse>(
            fullHarness, 46_251, nameof(TransfiniteGetScoreRewardResponse), "Transfinite duplicate score claim").Code,
            "Transfinite duplicate score claim rejection");
        InvokeRegisteredRequestHandler(nameof(TransfiniteGetScoreRewardRequest), fullHarness.Session, 46_252,
            new TransfiniteGetScoreRewardRequest { ScoreRewardIndex = [int.MaxValue] });
        AssertEqual(20008002, ReadResponsePayload<TransfiniteGetScoreRewardResponse>(
            fullHarness, 46_252, nameof(TransfiniteGetScoreRewardResponse), "Transfinite out-of-range score claim").Code,
            "Transfinite out-of-range score claim rejection");
        int unearned = Enumerable.Range(0, Math.Min(flowRewards.Score.Count, flowRewards.RewardId.Count))
            .First(index => flowRewards.Score[index] > 0 && flowRewards.RewardId[index] > 0
                && !nonmonotonicClaims.Contains(index));
        fullHarness.Session.inventory.Items.Single(item => item.Id == 105).Count = 0;
        InvokeRegisteredRequestHandler(nameof(TransfiniteGetScoreRewardRequest), fullHarness.Session, 46_253,
            new TransfiniteGetScoreRewardRequest { ScoreRewardIndex = [unearned] });
        AssertEqual(20008002, ReadResponsePayload<TransfiniteGetScoreRewardResponse>(
            fullHarness, 46_253, nameof(TransfiniteGetScoreRewardResponse), "Transfinite unearned score claim").Code,
            "Transfinite unearned score claim rejection");
    }

    private static void ValidateWheelchairManualCompatibility()
    {
        WheelchairManualActivityTable activity = TableReaderV2.Parse<WheelchairManualActivityTable>().Single();
        ConditionTable manualOpenCondition = TableReaderV2.Parse<ConditionTable>()
            .Single(condition => condition.Id == 770304);
        ConditionTable transferCondition = TableReaderV2.Parse<ConditionTable>()
            .Single(condition => condition.Id == 770306);
        AssertEqual(17424, manualOpenCondition.Type, "Wheelchair Manual client open condition type");
        AssertIntegerList([1], manualOpenCondition.Params.Select(Convert.ToInt64).ToArray(),
            "Wheelchair Manual client open condition parameter");
        AssertEqual(17421, transferCondition.Type, "Wheelchair Manual transfer condition type");
        AssertIntegerList([1], transferCondition.Params.Select(Convert.ToInt64).ToArray(),
            "Wheelchair Manual transfer condition parameter");
        HashSet<int> periodIds = TableReaderV2.Parse<WheelchairManualGuideActivityPeriodTable>()
            .Select(period => period.Id)
            .ToHashSet();
        List<int> expectedOpenActivityIds = TableReaderV2.Parse<WheelchairManualGuideActivityTable>()
            .Where(entry => periodIds.Contains(entry.PeriodIds))
            .Select(entry => entry.Id)
            .OrderBy(id => id)
            .ToList();
        if (expectedOpenActivityIds.Count == 0)
            throw new InvalidDataException("Wheelchair Manual has no authoritative guide activity entries.");

        MethodInfo builder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildWheelchairManualActivityPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            Type.EmptyTypes);
        NotifyWheelchairManualActivity fresh = (NotifyWheelchairManualActivity)(builder.Invoke(null, null)
            ?? throw new InvalidDataException("Wheelchair Manual fresh payload was null."));
        NotifyWheelchairManualActivity relog = (NotifyWheelchairManualActivity)(builder.Invoke(null, null)
            ?? throw new InvalidDataException("Wheelchair Manual relog payload was null."));

        AssertEqual(activity.Id, fresh.ActivityId, "Wheelchair Manual authoritative activity");
        AssertEqual(activity.PlanIds.Max(), fresh.PlanId, "Wheelchair Manual authoritative current plan");
        AssertIntegerList(expectedOpenActivityIds.Select(Convert.ToInt64).ToArray(),
            fresh.OpenActivityIds.Select(Convert.ToInt64).ToArray(), "Wheelchair Manual authoritative open activity ids");
        AssertEqual(1, fresh.BpLevel, "Wheelchair Manual fresh state level");
        AssertEqual(false, fresh.IsSeniorManualUnlock, "Wheelchair Manual fresh senior state");
        AssertEqual(0, fresh.GetRewardManualRewardIds.Count, "Wheelchair Manual fresh rewards");
        AssertEqual(0, fresh.GetRewardPlanIds.Count, "Wheelchair Manual fresh plan rewards");
        AssertEqual(0, fresh.FinishStageIds.Count, "Wheelchair Manual fresh teaching progress");
        AssertEqual(fresh.ActivityId, relog.ActivityId, "Wheelchair Manual relog activity stability");
        AssertIntegerList(fresh.OpenActivityIds.Select(Convert.ToInt64).ToArray(),
            relog.OpenActivityIds.Select(Convert.ToInt64).ToArray(), "Wheelchair Manual relog open activity stability");

        LottoTable lotto = TableReaderV2.Parse<LottoTable>()
            .Single(row => row.Id == activity.LottoId && row.TimeId == activity.TimeId);
        LottoPrimaryTable primary = TableReaderV2.Parse<LottoPrimaryTable>()
            .Single(row => row.TimeId == activity.TimeId && row.LottoIdList.Contains(lotto.Id));
        Dictionary<int, LottoRewardTable> rewards = TableReaderV2.Parse<LottoRewardTable>()
            .Where(row => row.LottoId == lotto.Id)
            .ToDictionary(row => row.Id);
        HashSet<int> ticketRules = TableReaderV2.Parse<LottoBuyTicketRuleTable>().Select(row => row.Id).ToHashSet();
        AssertEqual(true, rewards.Count > 0, "Wheelchair Lotto authoritative rewards");
        AssertEqual(true, lotto.BuyTicketRuleIdList.All(ticketRules.Contains),
            "Wheelchair Lotto authoritative ticket rules");

        LottoInfoResponse ReadLotto(Player player, long playerId, int packetId)
        {
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                $"wheelchair-lotto-{playerId}");
            InvokeRegisteredRequestHandler(nameof(LottoInfoRequest), harness.Session, packetId, new LottoInfoRequest());
            return ReadResponsePayload<LottoInfoResponse>(
                harness, packetId, nameof(LottoInfoResponse), "Wheelchair LottoInfo response");
        }

        Player lottoFresh = CreateDrawCompatibilityPlayer(46_200);
        LottoInfoResponse lottoFreshResponse = ReadLotto(lottoFresh, 46_200, 46_700);
        AssertEqual(0, lottoFreshResponse.Code, "Wheelchair LottoInfo fresh Code");
        AssertEqual(1, lottoFreshResponse.LottoInfos.Count, "Wheelchair LottoInfo fresh row count");
        LottoInfoResponse.LottoInfo freshLotto = lottoFreshResponse.LottoInfos.Single();
        AssertEqual(lotto.Id, freshLotto.Id, "Wheelchair LottoInfo table-derived Id");
        AssertEqual(primary.Id, freshLotto.LottoPrimaryId, "Wheelchair LottoInfo table-derived primary");
        AssertEqual(0, freshLotto.ExtraRewardState, "Wheelchair LottoInfo fresh extra reward");
        AssertEqual(0, freshLotto.LottoRewards.Count, "Wheelchair LottoInfo fresh claims");
        AssertEqual(0, freshLotto.LottoRecords.Count, "Wheelchair LottoInfo fresh records");
        JObject freshWire = JObject.Parse(MessagePackSerializer.ConvertToJson(
            MessagePackSerializer.Serialize(lottoFreshResponse)));
        JObject freshInfoWire = (JObject?)freshWire["LottoInfos"]?.Single()
            ?? throw new InvalidDataException("Wheelchair LottoInfo fresh wire row missing.");
        AssertEqual(5, freshInfoWire.Properties().Count(), "Wheelchair LottoInfo exact wire key count");
        AssertEqual(true, freshInfoWire.Properties().Select(property => property.Name).ToHashSet()
            .SetEquals(["Id", "LottoPrimaryId", "ExtraRewardState", "LottoRewards", "LottoRecords"]),
            "Wheelchair LottoInfo exact wire keys");
        AssertEqual(null, typeof(LottoInfoResponse.LottoRecord.LottoRewardGoods).GetProperty("ShowQuality"),
            "Wheelchair Lotto reward goods omits ShowQuality");
        MethodInfo selfChoiceBuilder = RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildSelfChoiceLottoPayload",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player)]);
        Dictionary<string, object?> freshSelfChoice = (Dictionary<string, object?>)(selfChoiceBuilder.Invoke(null, [lottoFresh])
            ?? throw new InvalidDataException("Wheelchair SelfChoice Lotto fresh payload was null."));
        AssertIntegerList([primary.Id], ((int[])freshSelfChoice["LottoPrimaryIds"]!).Select(Convert.ToInt64).ToArray(),
            "Wheelchair SelfChoice Lotto fresh table-derived primary");
        AssertEqual(0, ((Dictionary<int, int>)freshSelfChoice["SelectedPrimaryIdToLottoId"]!).Count,
            "Wheelchair SelfChoice Lotto fresh selection");

        LottoRewardTable[] seededRewards = rewards.Values.OrderBy(row => row.Id).Take(2).ToArray();
        Player lottoRelog = CreateDrawCompatibilityPlayer(46_201);
        lottoRelog.Lotto.Infos.Add(new LottoStateInfo
        {
            Id = lotto.Id,
            LottoPrimaryId = primary.Id,
            LottoRewards = seededRewards.Select(row => row.Id).ToList(),
            LottoRecords = seededRewards.Select((row, index) => new LottoStateRecord
            {
                RewardId = row.Id,
                LottoTime = index + 1
            }).ToList()
        });
        LottoInfoResponse lottoRelogResponse = ReadLotto(lottoRelog, 46_201, 46_701);
        AssertEqual(0, lottoRelogResponse.Code, "Wheelchair LottoInfo seeded relog Code");
        AssertIntegerList(seededRewards.Select(row => (long)row.Id).ToArray(),
            lottoRelogResponse.LottoInfos.Single().LottoRewards.Select(Convert.ToInt64).ToArray(),
            "Wheelchair LottoInfo seeded relog claims");
        AssertIntegerList(seededRewards.Select(row => (long)row.TemplateId).ToArray(),
            lottoRelogResponse.LottoInfos.Single().LottoRecords.Select(record => (long)record.RewardGoods.TemplateId).ToArray(),
            "Wheelchair LottoInfo seeded relog table-derived records");
        Dictionary<string, object?> relogSelfChoice = (Dictionary<string, object?>)(selfChoiceBuilder.Invoke(null, [lottoRelog])
            ?? throw new InvalidDataException("Wheelchair SelfChoice Lotto relog payload was null."));
        Dictionary<int, int> relogSelections = (Dictionary<int, int>)relogSelfChoice["SelectedPrimaryIdToLottoId"]!;
        AssertEqual(lotto.Id, relogSelections[primary.Id], "Wheelchair SelfChoice Lotto relog table-derived selection");

        Player invalidLotto = CreateDrawCompatibilityPlayer(46_202);
        invalidLotto.Lotto.Infos.Add(new LottoStateInfo
        {
            Id = lotto.Id,
            LottoPrimaryId = primary.Id,
            LottoRewards = [seededRewards[0].Id, seededRewards[0].Id]
        });
        LottoInfoResponse invalidLottoResponse = ReadLotto(invalidLotto, 46_202, 46_702);
        AssertEqual(1, invalidLottoResponse.Code, "Wheelchair LottoInfo rejects invalid persisted state");
    }
}
