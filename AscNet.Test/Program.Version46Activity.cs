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
                StageId = passedStageId,
                StageProgressIndex = 1,
                Score = 1,
                PassedStageIds = [passedStageId]
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
            AssertEqual(prepared.BattleInfo.Score, progressedBattle.StageInfo.Single().Score,
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
