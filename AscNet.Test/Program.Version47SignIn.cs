using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.signin;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    /// <summary>
    /// 4.7 generic sign-in compatibility: table-driven Type 1 daily + Type 2 event (Id 114/115)
    /// with per-sign durable state, 05:00 UTC business-day boundary, independent Id1/Id115
    /// progression, exact SignInReward→Reward→RewardGoods chain, idempotent claims, schedule/level
    /// gating, relogin persistence, and the NotifySignInData reset schema.
    /// </summary>
    private static void ValidateVersion47SignInCompatibility()
    {
        Type signInModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.SignInModule");
        MethodInfo buildLoginAt = RequiredMethod(
            signInModule, "BuildLoginSignInfos", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Player), typeof(DateTimeOffset)]);
        MethodInfo processRequest = RequiredMethod(
            signInModule, "ProcessSignInRequest", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Session), typeof(int), typeof(DateTimeOffset)]);
        MethodInfo buildResetPush = RequiredMethod(
            signInModule, "BuildNotifySignInData", BindingFlags.Static | BindingFlags.Public,
            [typeof(Player), typeof(DateTimeOffset)]);

        List<SignInfo> Build(Player player, DateTimeOffset now) =>
            (List<SignInfo>)(buildLoginAt.Invoke(null, [player, now])
                ?? throw new InvalidDataException("BuildLoginSignInfos returned no sign-in data."));
        SignInfo Info(Player player, DateTimeOffset now, int signId) =>
            Build(player, now).Single(info => info.Id == signId);
        SignInResponse Claim(Session session, int signId, DateTimeOffset now) =>
            (SignInResponse)(processRequest.Invoke(null, [session, signId, now])
                ?? throw new InvalidDataException("ProcessSignInRequest returned no response."));

        // Wire schema: reset push carries SignInfos; each SignInfo carries the retail 5-field map.
        AssertMailNamedMapKeys(typeof(NotifySignInData), null, ["SignInfos"], "NotifySignInData");
        AssertMailNamedMapKeys(typeof(SignInfo), null, ["Id", "Round", "Day", "Got", "FinishDay"], "SignInfo");

        // Table-driven fixtures: the cutover lands the 4.7 event rows and schedule windows.
        SignInTable sign114 = TableReaderV2.Parse<SignInTable>().Single(row => row.Id == 114);
        SignInTable sign115 = TableReaderV2.Parse<SignInTable>().Single(row => row.Id == 115);
        AssertEqual(2, sign114.Type, "SignIn 114 Type 2 event");
        AssertEqual(2, sign115.Type, "SignIn 115 Type 2 event");

        DateTimeOffset WindowStart(int timeId) => DateTimeOffset.FromUnixTimeSeconds(
            TableReaderV2.Parse<ActivityScheduleTable>().Single(row => row.Id == timeId).StartTime);
        // 06:00 UTC on the k-th business day after the event opens (past the 05:00 boundary).
        DateTimeOffset DayNow(long windowStart, int dayIndex) =>
            DateTimeOffset.FromUnixTimeSeconds(windowStart + (dayIndex * 86_400L) + 3_600L);
        long windowStart115 = WindowStart(sign115.TimeId
            ?? throw new InvalidDataException("SignIn 115 requires a TimeId.")).ToUnixTimeSeconds();
        DateTimeOffset now115 = DayNow(windowStart115, 0);
        DateTimeOffset now114Closed = now115; // Id 114's window has already ended by the 4.7 window.

        // Login inclusion/exclusion by schedule window and level gate.
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                   out RecordingMongoCollectionProxy<Player> playerCollection,
                   out RecordingMongoCollectionProxy<Character> characterCollection,
                   out RecordingMongoCollectionProxy<Inventory> inventoryCollection))
        {
            Player openPlayer = CreateDrawCompatibilityPlayer(47_115);
            AssertEqual(true, Build(openPlayer, now115).Any(info => info.Id == 1), "daily sign-in always in login");
            AssertEqual(true, Build(openPlayer, now115).Any(info => info.Id == 115), "open Id115 in login");
            AssertEqual(false, Build(openPlayer, now115).Any(info => info.Id == 114), "closed Id114 excluded from login");
            AssertEqual(1L, Info(openPlayer, now115, 115).Round, "Id115 login Round");
            AssertEqual(1L, Info(openPlayer, now115, 115).Day, "Id115 login Day");
            AssertEqual(false, Info(openPlayer, now115, 115).Got, "Id115 login Got fresh");
            AssertEqual(0L, Info(openPlayer, now115, 115).FinishDay, "Id115 login FinishDay neutral");

            // Id 115 before its window opens is excluded.
            AssertEqual(false,
                Build(openPlayer, DateTimeOffset.FromUnixTimeSeconds(windowStart115 - 1)).Any(info => info.Id == 115),
                "Id115 excluded before window opens");

            // Level gate: a player below OpenLevel is excluded even while the window is open.
            Player lowLevelPlayer = CreateDrawCompatibilityPlayer(47_115);
            lowLevelPlayer.PlayerData.Level = 5;
            AssertEqual(false, Build(lowLevelPlayer, now115).Any(info => info.Id == 115), "Id115 level-gated out of login");
            AssertEqual(true, Build(lowLevelPlayer, now115).Any(info => info.Id == 1), "daily sign-in ignores level gate");

            // Independent Id1/Id115 state: claiming the event never touches the daily log-in.
            Player independentPlayer = CreateDrawCompatibilityPlayer(47_115);
            Inventory inventory = CreateDrawCompatibilityInventory(47_115, []);
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(47_115), independentPlayer, inventory, "v47-sign-in-claim");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(47_115);

            SignInResponse eventClaim = Claim(harness.Session, 115, now115);
            AssertEqual(0, eventClaim.Code, "Id115 first claim Code");
            AssertId115Day1Reward(eventClaim, "Id115 first claim reward");
            AssertEqual(1L, independentPlayer.SignInStates.Single(s => s.Id == 115).ClaimCount, "Id115 claim count");
            AssertEqual(true, Info(independentPlayer, now115, 115).Got, "Id115 Got after claim");
            AssertEqual(false, Info(independentPlayer, now115, 1).Got, "Id1 Got unaffected by Id115 claim");
            AssertEqual(0L, independentPlayer.SignInStates.Single(s => s.Id == 1).ClaimCount, "Id1 claim count stays zero");
            AssertEqual(1L, Info(independentPlayer, now115, 1).Day, "Id1 Day unaffected by Id115 claim");
            AssertEqual(1L, inventory.Items.Single(item => item.Id == 90030).Count, "Id115 day-1 item in inventory");
            AssertEqual(1, playerCollection.ReplaceOneCalls, "Id115 claim persisted a player save");

            // Duplicate same-day Id115 claim: idempotent no-op, no mutation, no extra save.
            int savesBeforeDuplicate = playerCollection.ReplaceOneCalls;
            long itemCountBeforeDuplicate = inventory.Items.Single(item => item.Id == 90030).Count;
            SignInResponse duplicate = Claim(harness.Session, 115, now115);
            AssertEqual(0, duplicate.Code, "Id115 duplicate Code");
            AssertEmptyList(duplicate.RewardGoodsList, "Id115 duplicate RewardGoodsList");
            AssertEqual(savesBeforeDuplicate, playerCollection.ReplaceOneCalls, "Id115 duplicate does not persist");
            AssertEqual(itemCountBeforeDuplicate, inventory.Items.Single(item => item.Id == 90030).Count, "Id115 duplicate does not re-grant item");

            // Persistence/relogin: per-sign state survives a BSON round trip.
            Player reloaded = BsonSerializer.Deserialize<Player>(independentPlayer.ToBson());
            AssertEqual(1L, reloaded.SignInStates.Single(s => s.Id == 115).ClaimCount, "Id115 relogin claim count");
            AssertEqual(0L, reloaded.SignInStates.Single(s => s.Id == 1).ClaimCount, "Id1 relogin claim count");
            AssertEqual(true, Info(reloaded, now115, 115).Got, "Id115 relogin Got");
            AssertEqual(1L, Info(reloaded, now115, 1).Day, "Id1 relogin Day");

            // Id1 remains independently claimable; the daily path persists per-sign Id1 state.
            SignInResponse dailyClaim = Claim(harness.Session, 1, now115);
            AssertEqual(0, dailyClaim.Code, "daily sign-in Code");
            AssertEqual(1L, independentPlayer.SignInStates.Single(s => s.Id == 1).ClaimCount, "Id1 claim count after daily claim");
            AssertEqual(1L, independentPlayer.SignInStates.Single(s => s.Id == 115).ClaimCount, "Id115 count unaffected by daily claim");
        }

        // 05:00 UTC business-day boundary (pure login-builder, no harness needed).
        ValidateVersion47SignInBusinessDayBoundary(signInModule, buildLoginAt);

        // Completed events stay claimed after the business-day boundary, including after relogin.
        ValidateVersion47SignInCompletion(buildLoginAt, processRequest, buildResetPush, [sign115]);

        ValidateVersion47SignInPushes(signInModule, processRequest, buildLoginAt, buildResetPush, now115);

        // Inactive (closed schedule) and level-gated requests are rejected with no mutation.
        ValidateVersion47SignInRejections(signInModule, processRequest, now115, now114Closed);
    }

    private static void AssertId115Day1Reward(SignInResponse response, string name)
    {
        SignInRewardTable day1 = TableReaderV2.Parse<SignInRewardTable>()
            .Single(row => row.SignId == 115 && row.Round == 1 && row.Day == 1);
        RewardTable reward = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == day1.RewardId);
        RewardGoodsTable goods = TableReaderV2.Parse<RewardGoodsTable>()
            .Single(row => row.Id == reward.SubIds.Single());
        AssertEqual(1, response.RewardGoodsList.Count, $"{name} count");
        RewardGoods actual = response.RewardGoodsList[0];
        AssertEqual(goods.Id, actual.Id, $"{name} goods Id");
        AssertEqual(goods.TemplateId, actual.TemplateId, $"{name} TemplateId");
        AssertEqual(goods.Count, actual.Count, $"{name} Count");
        AssertEqual((int)RewardType.Item, actual.RewardType, $"{name} RewardType");
    }

    private static void ValidateVersion47SignInBusinessDayBoundary(
        Type signInModule, MethodInfo buildLoginAt)
    {
        List<SignInfo> Build(Player player, DateTimeOffset now) =>
            (List<SignInfo>)(buildLoginAt.Invoke(null, [player, now])
                ?? throw new InvalidDataException("BuildLoginSignInfos returned no sign-in data."));

        // Claim at 06:00 UTC on 2026-08-23 (business day 08-23 after the 05:00 boundary).
        long claimTime = DateTimeOffset.Parse("2026-08-23T06:00:00Z").ToUnixTimeSeconds();
        Player player = CreateDrawCompatibilityPlayer(47_115);
        player.SignInStates.Add(new PlayerSignInState { Id = 115, ClaimCount = 1, LastSignInTime = claimTime });

        // 04:59 UTC the next morning is still the same business day (before the 05:00 boundary).
        AssertEqual(true,
            Build(player, DateTimeOffset.Parse("2026-08-24T04:59:00Z")).Single(i => i.Id == 115).Got,
            "Id115 Got at 04:59 before 05:00 reset");
        // 05:00 UTC is a new business day.
        AssertEqual(false,
            Build(player, DateTimeOffset.Parse("2026-08-24T05:00:00Z")).Single(i => i.Id == 115).Got,
            "Id115 Got resets at 05:00 UTC");
        // Same-day later timestamp stays claimed.
        AssertEqual(true,
            Build(player, DateTimeOffset.Parse("2026-08-23T23:59:00Z")).Single(i => i.Id == 115).Got,
            "Id115 Got later same business day");
        // Next business day advances the claimable day.
        AssertEqual(2L,
            Build(player, DateTimeOffset.Parse("2026-08-24T06:00:00Z")).Single(i => i.Id == 115).Day,
            "Id115 Day advances after business-day boundary");
    }

    private static void ValidateVersion47SignInCompletion(
        MethodInfo buildLoginAt, MethodInfo processRequest, MethodInfo buildResetPush, SignInTable[] events)
    {
        SignInResponse Claim(Session session, int signId, DateTimeOffset now) =>
            (SignInResponse)(processRequest.Invoke(null, [session, signId, now])
                ?? throw new InvalidDataException("ProcessSignInRequest returned no response."));
        void AssertProgress(Player player, DateTimeOffset now, int signId, long round, long day, bool got, string name)
        {
            List<SignInfo> login = (List<SignInfo>)(buildLoginAt.Invoke(null, [player, now])
                ?? throw new InvalidDataException("BuildLoginSignInfos returned no sign-in data."));
            NotifySignInData reset = (NotifySignInData)(buildResetPush.Invoke(null, [player, now])
                ?? throw new InvalidDataException("BuildNotifySignInData returned no push."));
            foreach (var (infos, surface) in new[] { (login, "login"), (reset.SignInfos, "reset push") })
            {
                SignInfo info = infos.Single(info => info.Id == signId);
                AssertEqual(round, info.Round, $"{name} {surface} Round");
                AssertEqual(day, info.Day, $"{name} {surface} Day");
                AssertEqual(got, info.Got, $"{name} {surface} Got");
            }
        }

        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        SignInTable daily = TableReaderV2.Parse<SignInTable>().Single(row => row.Type == 1);
        foreach (SignInTable sign in events)
        {
            int totalDays = sign.RoundDays.Sum();
            long windowStart = TableReaderV2.Parse<ActivityScheduleTable>()
                .Single(row => row.Id == sign.TimeId).StartTime;
            DateTimeOffset firstDay = DateTimeOffset.FromUnixTimeSeconds(windowStart + 3_600L);
            Player player = CreateDrawCompatibilityPlayer(47_000 + sign.Id);
            Inventory inventory = CreateDrawCompatibilityInventory(player.PlayerData.Id, []);
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(player.PlayerData.Id), player, inventory, $"v47-sign-in-completion-{sign.Id}");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(player.PlayerData.Id);

            for (int day = 1; day <= totalDays; day++)
            {
                DateTimeOffset now = firstDay.AddDays(day - 1);
                if (day == totalDays)
                    AssertProgress(BsonSerializer.Deserialize<Player>(player.ToBson()), now,
                        sign.Id, 1, totalDays, false, $"Id{sign.Id} unclaimed final day");
                SignInResponse response = Claim(harness.Session, sign.Id, now);
                AssertEqual(0, response.Code, $"Id{sign.Id} day {day} claim Code");
                SignInRewardTable reward = TableReaderV2.Parse<SignInRewardTable>()
                    .Single(row => row.SignId == sign.Id && row.Round == 1 && row.Day == day);
                RewardTable rewardTable = TableReaderV2.Parse<RewardTable>().Single(row => row.Id == reward.RewardId);
                List<RewardGoodsTable> goods = rewardTable.SubIds.Select(id =>
                    TableReaderV2.Parse<RewardGoodsTable>().Single(row => row.Id == id)).ToList();
                AssertEqual(goods.Count, response.RewardGoodsList.Count, $"Id{sign.Id} day {day} reward count");
                for (int index = 0; index < goods.Count; index++)
                {
                    AssertEqual(goods[index].TemplateId, response.RewardGoodsList[index].TemplateId, $"Id{sign.Id} day {day} reward template");
                    AssertEqual(goods[index].Count, response.RewardGoodsList[index].Count, $"Id{sign.Id} day {day} reward quantity");
                }
            }

            DateTimeOffset finalDay = firstDay.AddDays(totalDays - 1);
            player.SignInStates.Add(new PlayerSignInState
            {
                Id = daily.Id, ClaimCount = daily.RoundDays.Sum(), LastSignInTime = finalDay.ToUnixTimeSeconds()
            });
            Player reloaded = BsonSerializer.Deserialize<Player>(player.ToBson());
            harness.Session.player = reloaded;
            AssertEqual((long)totalDays, reloaded.SignInStates.Single(s => s.Id == sign.Id).ClaimCount,
                $"Id{sign.Id} completed persisted claim count");
            AssertProgress(reloaded, finalDay, sign.Id, 1, totalDays, true, $"Id{sign.Id} claimed final day");
            DateTimeOffset resetTime = firstDay.AddDays(totalDays).AddHours(-1);
            AssertProgress(reloaded, resetTime, sign.Id, 1, totalDays, true, $"Id{sign.Id} completed next business day");
            AssertProgress(reloaded, resetTime, daily.Id, 2, 1, false, "daily next round remains claimable");

            string inventoryBefore = Convert.ToHexString(inventory.ToBson());
            string playerBefore = Convert.ToHexString(reloaded.ToBson());
            foreach (DateTimeOffset retryTime in new[] { finalDay, resetTime })
            {
                SignInResponse retry = Claim(harness.Session, sign.Id, retryTime);
                AssertEqual(retryTime == finalDay, retry.Code == 0, $"Id{sign.Id} completed retry Code");
                AssertEmptyList(retry.RewardGoodsList, $"Id{sign.Id} completed retry rewards");
                AssertEqual(inventoryBefore, Convert.ToHexString(inventory.ToBson()), $"Id{sign.Id} completed retry grants nothing");
                AssertEqual(playerBefore, Convert.ToHexString(reloaded.ToBson()), $"Id{sign.Id} completed retry leaves progress unchanged");
            }
        }
    }

    private static void ValidateVersion47SignInPushes(
        Type signInModule, MethodInfo processRequest, MethodInfo buildLoginAt, MethodInfo buildResetPush, DateTimeOffset now115)
    {
        List<SignInfo> Build(Player player, DateTimeOffset now) =>
            (List<SignInfo>)(buildLoginAt.Invoke(null, [player, now])
                ?? throw new InvalidDataException("BuildLoginSignInfos returned no sign-in data."));
        NotifySignInData Reset(Player player, DateTimeOffset now) =>
            (NotifySignInData)(buildResetPush.Invoke(null, [player, now])
                ?? throw new InvalidDataException("BuildNotifySignInData returned no push."));

        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        Player player = CreateDrawCompatibilityPlayer(47_115);
        Inventory inventory = CreateDrawCompatibilityInventory(47_115, []);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(47_115), player, inventory, "v47-sign-in-pushes");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(47_115);

        // The claim emits the inventory push before the response is returned; the packet
        // handler writes the response after ProcessSignInRequest completes, so push-before-response
        // is structural. Assert the push actually lands on the wire with the table reward.
        processRequest.Invoke(null, [harness.Session, 115, now115]);
        NotifyItemDataList itemPush = ReadPushPayload<NotifyItemDataList>(
            harness, nameof(NotifyItemDataList), "Id115 claim item push");
        AssertEqual(1, itemPush.ItemDataList.Count, "Id115 claim push item count");

        // Reset push schema: the full SignInfos replacement reproduces the login SignInfos.
        List<SignInfo> login = Build(player, now115);
        NotifySignInData reset = Reset(player, now115);
        AssertMailNamedMapKeys(reset, ["SignInfos"], "NotifySignInData push schema");
        AssertEqual(
            Convert.ToHexString(MessagePackSerializer.Serialize(typeof(NotifySignInData), reset)),
            Convert.ToHexString(MessagePackSerializer.Serialize(typeof(NotifySignInData),
                new NotifySignInData { SignInfos = login })),
            "NotifySignInData reset matches login SignInfos");
    }

    private static void ValidateVersion47SignInRejections(
        Type signInModule, MethodInfo processRequest, DateTimeOffset now115, DateTimeOffset now114Closed)
    {
        SignInResponse Claim(Session session, int signId, DateTimeOffset now) =>
            (SignInResponse)(processRequest.Invoke(null, [session, signId, now])
                ?? throw new InvalidDataException("ProcessSignInRequest returned no response."));

        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerCollection, out _, out _);

        // Unknown id: never Code=0 with an empty list.
        Player player = CreateDrawCompatibilityPlayer(47_115);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(47_115), player, CreateDrawCompatibilityInventory(47_115, []), "v47-sign-in-reject");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(47_115);
        int saves = playerCollection.ReplaceOneCalls;
        SignInResponse unknown = Claim(harness.Session, 9_999_999, now115);
        AssertEqual(true, unknown.Code != 0, "unknown sign-in id rejected");
        AssertEmptyList(unknown.RewardGoodsList, "unknown sign-in id RewardGoodsList");
        AssertEqual(saves, playerCollection.ReplaceOneCalls, "unknown sign-in id does not persist");

        // Inactive (closed schedule) Id114 rejected with no mutation.
        byte[] protectedState = player.ToBson();
        SignInResponse closed = Claim(harness.Session, 114, now114Closed);
        AssertEqual(true, closed.Code != 0, "closed Id114 rejected");
        AssertEmptyList(closed.RewardGoodsList, "closed Id114 RewardGoodsList");
        AssertEqual(saves, playerCollection.ReplaceOneCalls, "closed Id114 does not persist");
        AssertEqual(Convert.ToHexString(protectedState), Convert.ToHexString(player.ToBson()), "closed Id114 no mutation");

        // Level-gated Id115 rejected with no mutation.
        Player lowLevel = CreateDrawCompatibilityPlayer(47_115);
        lowLevel.PlayerData.Level = 5;
        using LoopbackSessionHarness lowHarness = new(
            CreateDrawCompatibilityCharacter(47_115), lowLevel, CreateDrawCompatibilityInventory(47_115, []), "v47-sign-in-level");
        lowHarness.Session.stage = CreateLoginAccountCompatibilityStage(47_115);
        byte[] lowState = lowLevel.ToBson();
        SignInResponse levelGated = Claim(lowHarness.Session, 115, now115);
        AssertEqual(true, levelGated.Code != 0, "level-gated Id115 rejected");
        AssertEmptyList(levelGated.RewardGoodsList, "level-gated Id115 RewardGoodsList");
        AssertEqual(Convert.ToHexString(lowState), Convert.ToHexString(lowLevel.ToBson()), "level-gated Id115 no mutation");
    }
}
