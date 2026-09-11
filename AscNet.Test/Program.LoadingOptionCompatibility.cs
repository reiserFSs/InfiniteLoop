using System.Globalization;
using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.client.loading;
using AscNet.Table.V2.share.archive;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.config;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateLoadingOptionCompatibility()
    {
        List<CGDetailTable> catalog = TableReaderV2.Parse<CGDetailTable>();
        HashSet<int> blockedGroups = TableReaderV2.Parse<CustomLoadingTable>().SelectMany(row => row.BlockGroup).ToHashSet();
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        int cap = int.Parse(TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == "CustomLoadingMaxSize").Value,
            CultureInfo.InvariantCulture);
        bool NoTime(CGDetailTable row) => string.IsNullOrWhiteSpace(row.UnLockTime) || row.UnLockTime == "0";
        bool Allowed(CGDetailTable row) => !blockedGroups.Contains(row.GroupId);
        int[] defaults = catalog.Where(row => Allowed(row) && NoTime(row) && row.Condition is null or 0)
            .OrderBy(row => row.Id).Select(row => row.Id).Take(cap + 1).ToArray();
        if (defaults.Length <= cap || cap < 2)
            throw new InvalidDataException("Loading fixtures require more default CGs than the configured cap.");
        CGDetailTable stageCg = catalog.First(row => Allowed(row) && NoTime(row) && row.Condition is > 0
            && conditions.TryGetValue(row.Condition.Value, out ConditionTable? condition)
            && condition.Type == 10105 && condition.Params.Count > 0);
        int stageId = conditions[stageCg.Condition!.Value].Params[0];
        CGDetailTable timedCg = catalog.First(row => Allowed(row) && !NoTime(row)
            && row.Condition is > 0 && conditions.TryGetValue(row.Condition.Value, out ConditionTable? condition)
            && condition.Type == 10105 && condition.Params.Count > 0
            && DateTimeOffset.TryParse(row.UnLockTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset time) && time < DateTimeOffset.UtcNow);
        DateTimeOffset unlockTime = DateTimeOffset.Parse(timedCg.UnLockTime
            ?? throw new InvalidDataException("Timed CG fixture lacks its unlock timestamp."), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        int blocked = catalog.First(row => blockedGroups.Contains(row.GroupId)).Id;
        int unknown = checked(catalog.Max(row => row.Id) + 1);
        Type archiveModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.ArchiveCgModule");
        MethodInfo unlocked = RequiredMethod(archiveModule, "GetUnlockedCgs", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(DateTimeOffset)]);
        MethodInfo reconcile = RequiredMethod(archiveModule, "Reconcile", BindingFlags.Static | BindingFlags.Public,
            [typeof(Session), typeof(bool)]);
        HashSet<int> Earned(Session session, DateTimeOffset now) =>
            (HashSet<int>)unlocked.Invoke(null, [session, now])!;

        const long uid = 48_770;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(
            out RecordingMongoCollectionProxy<Player> saves, out _, out RecordingMongoCollectionProxy<Inventory> inventorySaves, out _);
        BsonDocument legacy = CreateDrawCompatibilityPlayer(uid).ToBsonDocument();
        legacy.Remove("loading_option");
        legacy.Remove("archive_unlocked_cgs");
        Player player = BsonSerializer.Deserialize<Player>(legacy);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, [new Item { Id = Inventory.Coin, Count = 321 }]), "loading-option-compat");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);

        HashSet<int> beforeTime = Earned(harness.Session, unlockTime.AddSeconds(-1));
        HashSet<int> atTime = Earned(harness.Session, unlockTime);
        AssertEqual(false, beforeTime.Contains(timedCg.Id), "Archive timed CG locked before deadline");
        AssertEqual(true, atTime.Contains(timedCg.Id), "Archive timed CG unlocks at inclusive deadline");
        AssertEqual(false, atTime.Contains(stageCg.Id), "Archive stage CG requires actual stage progress");
        AssertEqual(0, player.ArchiveUnlockedCgs.Count, "Archive earned query does not mutate durable ownership");
        int timedStageId = conditions[timedCg.Condition!.Value].Params[0];
        harness.Session.stage.Stages[timedStageId] = new StageDatum { StageId = timedStageId, Passed = true };
        AssertEqual(true, Earned(harness.Session, unlockTime.AddSeconds(-1)).Contains(timedCg.Id),
            "Archive time and stage condition are alternatives, not conjunctive gates");
        harness.Session.stage.Stages.Remove(timedStageId);

        Dictionary<string, Packet.Push> login = ReadLoadingCompatibilityLogin(harness);
        AssertLoadingLogin(login, 1, []);
        NotifyArchiveLoginData archiveLogin = MessagePackSerializer.Deserialize<NotifyArchiveLoginData>(login[nameof(NotifyArchiveLoginData)].Content);
        HashSet<int> picker = archiveLogin.UnlockCgs.Select(Convert.ToInt32).ToHashSet();
        AssertEqual(true, defaults.All(picker.Contains), "Archive login exposes default-earned CGs to custom picker");
        AssertEqual(true, picker.Contains(timedCg.Id), "Archive login exposes time-earned CG to custom picker");
        AssertEqual(false, picker.Contains(stageCg.Id), "Archive login does not fabricate stage ownership");
        AssertEqual(true, picker.SetEquals(player.ArchiveUnlockedCgs), "Archive login ownership persisted before projection");
        string inventoryBefore = Convert.ToHexString(harness.Session.inventory.ToBson());
        int inventoryWrites = inventorySaves.ReplaceOneCalls;
        int packetId = 48_770;

        Set(1, [defaults[0]], true, "mode one remembers custom list");
        Set(2, [defaults[0]], true, "switch to custom mode");
        Set(2, [defaults[0], defaults[1]], true, "add CG");
        Set(2, [defaults[1], defaults[0]], true, "reorder CGs");
        int retrySaves = saves.ReplaceOneCalls;
        Set(2, [defaults[1], defaults[0]], true, "identical retry");
        AssertEqual(retrySaves, saves.ReplaceOneCalls, "identical retry does not rewrite player");
        Set(2, [defaults[1]], true, "remove CG");
        Set(2, [], true, "empty custom list remains valid");
        AssertLoadingLogin(ReadLoadingCompatibilityLogin(harness), 2, []);
        const string clearRequest = """{"LoadingData":{"LoadingType":2,"CgIds":null}}""";
        Set(1, [defaults[1], defaults[0]], true, "prepare client clear");
        saves.ThrowOnReplaceOne = true;
        try
        {
            Set(2, [], false, "client clear save failure restores mode and ordered list", clearRequest, 2);
        }
        finally
        {
            saves.ThrowOnReplaceOne = false;
        }
        Set(2, [], true, "client nil CG list clears remembered rotation", clearRequest, 0);
        harness.Session.player = player = BsonSerializer.Deserialize<Player>((saves.LastReplacement
            ?? throw new InvalidDataException("Client clear was not persisted.")).ToBson());
        AssertLoadingLogin(ReadLoadingCompatibilityLogin(harness), 2, []);
        Set(2, [defaults[0]], true, "restore selection before malformed requests");
        Set(2, [], false, "nonempty CG map is not a clear",
            """{"LoadingData":{"LoadingType":2,"CgIds":{"1":1}}}""", 5);
        Set(2, [], false, "empty CG map is not the client nil representation",
            """{"LoadingData":{"LoadingType":2,"CgIds":{}}}""", 5);
        Set(2, [], false, "scalar CG value is not a clear",
            """{"LoadingData":{"LoadingType":2,"CgIds":false}}""", 5);
        Set(2, [], false, "malformed CG array is not a clear",
            """{"LoadingData":{"LoadingType":2,"CgIds":["invalid"]}}""", 5);
        Set(2, [], false, "nil loading data cannot clear preferences",
            """{"LoadingData":null}""", 5);
        Set(3, [], false, "nil list does not bypass mode validation",
            """{"LoadingData":{"LoadingType":3,"CgIds":null}}""", 5);
        Set(2, defaults.Take(cap).ToList(), true, "configured cap accepted");
        Set(2, defaults.ToList(), false, "over cap rejected");
        Set(2, [defaults[0], defaults[0]], false, "duplicates rejected");
        Set(2, [unknown], false, "unknown CG rejected");
        Set(2, [stageCg.Id], false, "unearned CG rejected");
        player.ArchiveUnlockedCgs.Add(blocked);
        Set(2, [blocked], false, "blocked group rejected even when explicitly owned");
        player.ArchiveUnlockedCgs.Remove(blocked);
        Set(0, [], false, "invalid mode rejected");
        Set(3, [], false, "unknown mode rejected");
        Set(1, [defaults[1], defaults[0]], true, "remember ordered list in default mode");
        AssertLoadingLogin(ReadLoadingCompatibilityLogin(harness), 1, [defaults[1], defaults[0]]);
        saves.ThrowOnReplaceOne = true;
        Set(2, [defaults[0]], false, "save failure rolls back both mode and list");
        saves.ThrowOnReplaceOne = false;
        Set(2, [defaults[0]], true, "save failure retry succeeds");
        DispatchReplay("warm postrequest task snapshot");
        DrainWithoutArchive("warm postrequest task snapshot");

        harness.Session.stage.Stages[stageId] = new StageDatum { StageId = stageId, Passed = true, PassTimesTotal = 1 };
        HashSet<int> previousPicker = new(picker);
        saves.ThrowOnReplaceOne = true;
        try
        {
            reconcile.Invoke(null, [harness.Session, true]);
            throw new InvalidDataException("Archive save failure did not propagate.");
        }
        catch (TargetInvocationException)
        {
        }
        finally
        {
            saves.ThrowOnReplaceOne = false;
        }
        AssertEqual(true, previousPicker.SetEquals(player.ArchiveUnlockedCgs), "Archive failed save restores ownership");
        AssertNoAvailablePacket(harness, "Archive failed save does not advertise unlock");
        saves.ThrowOnReplaceOne = true;
        try
        {
            DispatchReplay("archive postrequest save failure preserves successful setting response");
            DrainWithoutArchive("failed postrequest archive reconciliation");
            AssertEqual(true, previousPicker.SetEquals(player.ArchiveUnlockedCgs),
                "postrequest archive failure retains old durable ownership");
        }
        finally
        {
            saves.ThrowOnReplaceOne = false;
        }
        DispatchReplay("next request retries failed archive reconciliation");
        NotifyArchiveCgs delta = ReadPushPayload<NotifyArchiveCgs>(harness, nameof(NotifyArchiveCgs),
            "stage-earned Archive delta", maxPacketsToRead: 32);
        int[] deltaIds = delta.UnlockCgs.Select(Convert.ToInt32).ToArray();
        AssertEqual(true, deltaIds.Contains(stageCg.Id), "stage progression sends picker unlock");
        AssertEqual(false, deltaIds.Any(previousPicker.Contains), "incremental archive push contains only new unlocks");
        picker.UnionWith(deltaIds);
        AssertEqual(true, picker.SetEquals(player.ArchiveUnlockedCgs), "client incremental union matches durable ownership");
        reconcile.Invoke(null, [harness.Session, true]);
        AssertNoAvailablePacket(harness, "identical archive reconciliation has no duplicate notification");
        Set(2, [stageCg.Id, defaults[1]], true, "stage-earned CG selectable without seeded unlock");

        Player persisted = BsonSerializer.Deserialize<Player>((saves.LastReplacement
            ?? throw new InvalidDataException("Loading preference save was not recorded.")).ToBson());
        harness.Session.player = player = persisted;
        Dictionary<string, Packet.Push> relog = ReadLoadingCompatibilityLogin(harness);
        AssertLoadingLogin(relog, 2, [stageCg.Id, defaults[1]]);
        NotifyArchiveLoginData reloadedArchive = MessagePackSerializer.Deserialize<NotifyArchiveLoginData>(relog[nameof(NotifyArchiveLoginData)].Content);
        AssertEqual(true, reloadedArchive.UnlockCgs.Contains(checked((uint)stageCg.Id)), "BSON relog retains stage-earned picker ownership");
        AssertEqual(inventoryBefore, Convert.ToHexString(harness.Session.inventory.ToBson()), "loading edits and archive unlocks never charge inventory");
        AssertEqual(inventoryWrites, inventorySaves.ReplaceOneCalls, "loading edits never persist a currency charge");

        void DispatchReplay(string name)
        {
            MethodInfo dispatch = RequiredMethod(typeof(Session), "InvokeRequestHandler",
                BindingFlags.Instance | BindingFlags.NonPublic, [typeof(RequestPacketHandlerDelegate), typeof(Packet.Request)]);
            dispatch.Invoke(harness.Session, [GetRegisteredRequestHandler(nameof(SettingLoadingOptionRequest)), new Packet.Request
            {
                Id = ++packetId,
                Name = nameof(SettingLoadingOptionRequest),
                Content = MessagePackSerializer.Serialize(new SettingLoadingOptionRequest
                {
                    LoadingData = new LoadingOptionData
                    {
                        LoadingType = player.LoadingOption.LoadingType,
                        CgIds = player.LoadingOption.CgIds.ToList()
                    }
                })
            }]);
            AssertEqual(0, ReadResponsePayload<SettingLoadingOptionResponse>(harness, packetId,
                nameof(SettingLoadingOptionResponse), name, maxPacketsToRead: 32).Code, name);
        }

        void DrainWithoutArchive(string name)
        {
            while (harness.TryReadAvailablePacket(name, out Packet packet))
            {
                AssertEqual(Packet.ContentType.Push, packet.Type, name + " no duplicate response");
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                AssertEqual(false, push.Name == nameof(NotifyArchiveCgs), name + " no unpersisted unlock notification");
            }
        }

        void Set(int mode, List<int> ids, bool success, string name, string? rawJson = null, int? expectedCode = null)
        {
            int oldMode = player.LoadingOption.LoadingType;
            int[] oldIds = player.LoadingOption.CgIds.ToArray();
            int oldSaves = saves.ReplaceOneCalls;
            GetRegisteredRequestHandler(nameof(SettingLoadingOptionRequest)).Invoke(harness.Session, new Packet.Request
            {
                Id = ++packetId,
                Name = nameof(SettingLoadingOptionRequest),
                Content = rawJson is null
                    ? MessagePackSerializer.Serialize(new SettingLoadingOptionRequest
                    {
                        LoadingData = new LoadingOptionData { LoadingType = mode, CgIds = ids }
                    })
                    : MessagePackSerializer.ConvertFromJson(rawJson)
            });
            SettingLoadingOptionResponse response = ReadResponsePayload<SettingLoadingOptionResponse>(harness,
                packetId, nameof(SettingLoadingOptionResponse), name);
            AssertEqual(success, response.Code == 0, name + " response");
            if (expectedCode.HasValue)
                AssertEqual(expectedCode.Value, response.Code, name + " exact response code");
            AssertEqual(success ? mode : oldMode, player.LoadingOption.LoadingType, name + " active mode");
            AssertIntegerList((success ? ids : oldIds.ToList()).Select(Convert.ToInt64).ToArray(),
                player.LoadingOption.CgIds.Select(Convert.ToInt64).ToArray(), name + " ordered list");
            if (!success && !saves.ThrowOnReplaceOne)
                AssertEqual(oldSaves, saves.ReplaceOneCalls, name + " rejects before persistence");
            AssertNoAvailablePacket(harness, name + " no extra success or currency push");
        }
    }

    private static Dictionary<string, Packet.Push> ReadLoadingCompatibilityLogin(LoopbackSessionHarness harness)
    {
        MethodInfo login = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "DoLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(bool)]);
        login.Invoke(null, [harness.Session, false]);
        Dictionary<string, Packet.Push> pushes = new(StringComparer.Ordinal);
        for (int index = 0; index < 192; index++)
        {
            Packet packet = harness.ReadPacket("loading compatibility startup");
            AssertEqual(Packet.ContentType.Push, packet.Type, "loading login packet type");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
            pushes.TryAdd(push.Name, push);
            if (push.Name == "NotifyWheelchairManualActivityUpdate")
            {
                AssertEqual(true, pushes.ContainsKey(nameof(NotifySettingLoadingOption)), "login delivers loading preferences");
                AssertEqual(true, pushes.ContainsKey(nameof(NotifyArchiveLoginData)), "login delivers usable archive catalog");
                return pushes;
            }
        }
        throw new InvalidDataException("Loading login did not reach startup tail.");
    }

    private static void AssertLoadingLogin(Dictionary<string, Packet.Push> pushes, int mode, int[] ids)
    {
        byte[] content = pushes[nameof(NotifySettingLoadingOption)].Content;
        JObject payload = JObject.Parse(MessagePackSerializer.ConvertToJson(content));
        AssertEqual(true, payload["LoadingData"] is JObject, "loading client receives nested LoadingData map");
        JObject data = (JObject)payload["LoadingData"]!;
        AssertEqual(JTokenType.Integer, data["LoadingType"]!.Type, "loading mode wire integer");
        AssertEqual(JTokenType.Array, data["CgIds"]!.Type, "loading CG list wire array including empty");
        AssertEqual(mode, data["LoadingType"]!.Value<int>(), "login restores loading mode");
        AssertIntegerList(ids.Select(Convert.ToInt64).ToArray(), data["CgIds"]!.Select(id =>
            id.Type == JTokenType.Integer ? id.Value<long>()
                : throw new InvalidDataException("Loading CG IDs must be wire integers.")).ToArray(),
            "login restores ordered remembered custom list");
    }
}
