using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;
using TeachingActivityTable = AscNet.Table.V2.share.fuben.teaching.TeachingActivityTable;

namespace AscNet.Test;

internal static partial class Program
{
    // TeachingActivity 51 (TimeId 912, ActivitySchedule 1790676000..1793750340 from the 4.8 notice).
    // Boundary claims inject times inside/outside that source window; the registered-success phase
    // uses an in-memory synthetic open window restored in finally.
    private static void ValidateVersion48TeachingTreasureClaims()
    {
        using MongoCollectionOverride rewardOverride = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Character> characterCollection,
            out RecordingMongoCollectionProxy<AscNet.Common.Database.Inventory> inventoryCollection);
        TeachingActivityTable activity = TableReaderV2.Parse<TeachingActivityTable>().Single(row => row.Id == 51);
        AssertEqual(912, activity.TimeId, "Teaching 51 source TimeId");
        AssertEqual(true, ActivityScheduleService.TryGet(912, out ActivityScheduleEntry window) && window.EndTime > 0, "Teaching 912 dated");

        const long playerId = 99_749;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []), "teaching-treasure-48-test");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.StudyProgressModule");
        MethodInfo claim = RequiredMethod(module, "ClaimTreasure", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Session), typeof(int), typeof(DateTimeOffset)]);
        MethodInfo resume = RequiredMethod(module, "ResumePartialTreasureClaims", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        MethodInfo login = RequiredMethod(module, "SendLoginState", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        long inside = window.StartTime + 3600;
        int packetId = 83_000;
        int Claim(int id, long at)
        {
            object result = claim.Invoke(null, [harness.Session, id, DateTimeOffset.FromUnixTimeSeconds(at)])!;
            return (int)result.GetType().GetField("Item1")!.GetValue(result)!;
        }
        void Stage(long id, long stars) => harness.Session.stage.Stages[id] = new StageDatum { StageId = id, Passed = true, StarsMark = stars };
        long Item(int id) => harness.Session.inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0;
        bool Receipts(int id)
        {
            string key = $"teaching-treasure:{playerId}:{id}";
            return harness.Session.inventory.AppliedRewardClaims.Contains(key) && harness.Session.character.AppliedRewardClaims.Contains(key);
        }
        // LastReplacement is recorded before an injected throw; durable state is the last successful write.
        void Reload()
        {
            harness.Session.inventory = BsonSerializer.Deserialize<AscNet.Common.Database.Inventory>(
                inventoryCollection.LastSuccessfulReplacementBson ?? harness.Session.inventory.ToBson());
            harness.Session.character = BsonSerializer.Deserialize<AscNet.Common.Database.Character>(
                characterCollection.LastSuccessfulReplacementBson ?? harness.Session.character.ToBson());
        }
        (TeachingTreasureRewardResponse Response, List<string> Pushes) Registered(int id)
        {
            InvokeRegisteredRequestHandler("TeachingTreasureRewardRequest", harness.Session, ++packetId, new TeachingTreasureRewardRequest { TreasureId = id });
            List<string> pushes = [];
            for (int index = 0; index < 16; index++)
            {
                Packet packet = harness.ReadPacket($"Teaching claim {id} packet {index}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content).Name);
                    continue;
                }
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(packetId, response.Id, "Teaching response correlation");
                AssertEqual(nameof(TeachingTreasureRewardResponse), response.Name, "Teaching response name");
                return (MessagePackSerializer.Deserialize<TeachingTreasureRewardResponse>(response.Content), pushes);
            }
            throw new InvalidDataException($"Missing TeachingTreasureRewardResponse for {id}.");
        }

        AssertEqual(20107004, Registered(int.MaxValue).Response.Code, "Teaching invalid treasure (registered)");

        // Window boundaries (begin <= now < end) and invalid ids reject before mutation.
        Stage(30100237, 0);
        AssertEqual(20107001, Claim(284, window.StartTime - 1), "Teaching claim before window");
        AssertEqual(20107002, Claim(284, window.EndTime), "Teaching claim at window end");
        AssertEqual(20107004, Claim(0, inside), "Teaching claim id 0");
        AssertEqual(20107009, Claim(285, inside), "Teaching Type2 stage not passed");

        // Type 2 at window start; character save fails after inventory persisted.
        long before = Item(30013);
        characterCollection.ThrowOnReplaceOne = true;
        bool failed = false;
        try { Claim(284, window.StartTime); }
        catch (TargetInvocationException) { failed = true; }
        finally { characterCollection.ThrowOnReplaceOne = false; }
        AssertEqual(true, failed, "Teaching partial save failure reached persistence");

        // Relogin after the window closed: the request path is closed, the login hook converges the receipt.
        Reload();
        AssertEqual(false, Receipts(284), "Teaching partial claim has a single receipt");
        AssertEqual(before + 1, Item(30013), "Teaching partial save credited inventory once");
        AssertEqual(20107002, Claim(284, window.EndTime + 1), "Teaching closed window rejects new request");
        resume.Invoke(null, [harness.Session]);
        Reload();
        AssertEqual(true, Receipts(284), "Teaching login resume writes both receipts");
        AssertEqual(before + 1, Item(30013), "Teaching login resume does not duplicate goods");
        AssertEqual(20003001, Claim(284, inside), "Teaching duplicate after resume");

        // Type 1 sums ChallengeStage stars: 2 stars meets 287 (RequireStar 2) but not 288 (5).
        Stage(30100240, 3);
        AssertEqual(20003009, Claim(288, inside), "Teaching Type1 insufficient stars");
        AssertEqual(0, Claim(287, inside), "Teaching Type1 at star boundary");
        Stage(30100241, 7);
        AssertEqual(0, Claim(288, inside), "Teaching Type1 at 5 stars");
        AssertEqual(20003009, Claim(289, inside), "Teaching Type1 8 stars not met");

        // Registered success through the real handler inside a synthetic open 912 window.
        ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
        int scheduleIndex = Array.FindIndex(schedules, row => row.Id == 912);
        schedules[scheduleIndex] = window with { StartTime = 0, EndTime = 0, Source = "synthetic-test:teaching-treasure-open" };
        try
        {
            Stage(30100238, 0);
            long itemBefore = Item(30013);
            (TeachingTreasureRewardResponse success, List<string> pushes) = Registered(285);
            AssertEqual(0, success.Code, "Teaching registered claim succeeds");
            AssertEqual(3, success.RewardGoodsList.Count, "Teaching Reward 12051 goods returned");
            AssertEqual(true, pushes.Contains(nameof(NotifyItemDataList)), "Teaching reward item push precedes response");
            AssertEqual(itemBefore + 1, Item(30013), "Teaching registered claim credits once");
            (TeachingTreasureRewardResponse duplicate, List<string> duplicatePushes) = Registered(285);
            AssertEqual(20003001, duplicate.Code, "Teaching registered duplicate rejected");
            AssertEqual(0, duplicate.RewardGoodsList.Count + duplicatePushes.Count, "Teaching duplicate grants nothing");

            // A committed Character receipt can lose its acknowledgement. The registered handler must
            // fail closed so this session cannot continue from an uncertain in-memory snapshot.
            const long stalePlayerId = 99_750;
            const string staleSessionId = "teaching-treasure-stale-session";
            byte[]? mainInventoryReplacement = inventoryCollection.LastSuccessfulReplacementBson;
            byte[]? mainCharacterReplacement = characterCollection.LastSuccessfulReplacementBson;

            using (LoopbackSessionHarness stale = new(CreateDrawCompatibilityCharacter(stalePlayerId),
                       CreateDrawCompatibilityPlayer(stalePlayerId),
                       CreateDrawCompatibilityInventory(stalePlayerId, []), staleSessionId))
            {
                stale.Session.stage = CreateLoginAccountCompatibilityStage(stalePlayerId);
                stale.Session.stage.Stages[30100237] = new StageDatum
                {
                    StageId = 30100237,
                    Passed = true
                };
                AssertEqual(true, Server.Instance.Sessions.TryAdd(staleSessionId, stale.Session),
                    "Teaching stale-session failure registers the session");
                try
                {
                    characterCollection.ThrowAfterReplaceOne = true;
                    bool acknowledgementLost = false;
                    try
                    {
                        InvokeRegisteredRequestHandler(nameof(TeachingTreasureRewardRequest), stale.Session, 83_001,
                            new TeachingTreasureRewardRequest { TreasureId = 284 });
                    }
                    catch (InvalidDataException exception) when (exception.InnerException is MongoDB.Driver.MongoException)
                    {
                        acknowledgementLost = true;
                    }
                    finally
                    {
                        characterCollection.ThrowAfterReplaceOne = false;
                    }

                    AssertEqual(true, acknowledgementLost, "Teaching committed acknowledgement loss reaches the registered handler");
                    AssertEqual(false, Server.Instance.Sessions.ContainsKey(staleSessionId),
                        "Teaching handler disconnects the stale session");
                    AssertEqual(false, stale.TryReadAvailablePacket("Teaching failed registered claim packet", out _),
                        "Teaching failed registered claim emits no success packet");

                    string claimKey = $"teaching-treasure:{stalePlayerId}:284";
                    AscNet.Common.Database.Inventory durableInventory = BsonSerializer.Deserialize<AscNet.Common.Database.Inventory>(
                        inventoryCollection.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("Teaching acknowledgement loss did not persist Inventory."));
                    AscNet.Common.Database.Character durableCharacter = BsonSerializer.Deserialize<AscNet.Common.Database.Character>(
                        characterCollection.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("Teaching acknowledgement loss did not persist Character."));
                    AssertEqual(true, durableInventory.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal)
                        && durableCharacter.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal),
                        "Teaching acknowledgement loss durably records both receipts");
                    AssertEqual(1L, durableInventory.Items.Single(item => item.Id == 30013).Count,
                        "Teaching acknowledgement loss durably credits one reward");

                    using LoopbackSessionHarness reload = new(durableCharacter,
                        BsonSerializer.Deserialize<AscNet.Common.Database.Player>(stale.Session.player.ToBson()),
                        durableInventory, "teaching-treasure-stale-reload");
                    reload.Session.stage = CreateLoginAccountCompatibilityStage(stalePlayerId);
                    InvokeRegisteredRequestHandler(nameof(TeachingTreasureRewardRequest), reload.Session, 83_002,
                        new TeachingTreasureRewardRequest { TreasureId = 284 });
                    TeachingTreasureRewardResponse reloadedDuplicate = ReadResponsePayload<TeachingTreasureRewardResponse>(
                        reload, 83_002, nameof(TeachingTreasureRewardResponse),
                        "Teaching stale-session reload duplicate response");
                    AssertEqual(20003001, reloadedDuplicate.Code,
                        "Teaching fresh reload rejects the durably completed claim");
                    AssertEqual(0, reloadedDuplicate.RewardGoodsList.Count,
                        "Teaching fresh reload does not duplicate the reward");
                    AssertEqual(false, reload.TryReadAvailablePacket("Teaching stale-session reload packet", out _),
                        "Teaching fresh reload emits no reward push");
                }
                finally
                {
                    Server.Instance.Sessions.TryRemove(staleSessionId, out _);
                    inventoryCollection.LastSuccessfulReplacementBson = mainInventoryReplacement;
                    characterCollection.LastSuccessfulReplacementBson = mainCharacterReplacement;
                }
            }
        }
        finally { schedules[scheduleIndex] = window; }

        // BSON reload: login TreasureRecord derives from durable receipts.
        Reload();
        while (harness.TryReadAvailablePacket("Teaching drain", out _)) { }
        login.Invoke(null, [harness.Session]);
        JObject? info = null;
        while (info is null && harness.TryReadAvailablePacket("Teaching login push", out Packet packet))
        {
            if (packet.Type != Packet.ContentType.Push) continue;
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
            if (push.Name == "NotifyTeachingActivityInfo")
                info = JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content));
        }
        JObject entry = info!["ActivityInfo"]!.OfType<JObject>().Single(row => row.Value<int>("Id") == 51);
        AssertIntegerList([284, 285, 287, 288], entry["TreasureRecord"]!.Select(id => id.Value<long>()).Order().ToArray(), "Teaching TreasureRecord after reload");
        Console.WriteLine("Teaching treasure 4.8 passed: boundaries, registered success/duplicate, closed-window login resume, reload TreasureRecord.");
    }
}
