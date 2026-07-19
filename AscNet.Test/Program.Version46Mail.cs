using System.Buffers;
using System.Reflection;
using AscNet.Common.Database;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.client.mail;
using AscNet.Table.V2.share.mail;
using MailGetRewardRequest = AscNet.Common.MsgPack.MailGetRewardRequest;
using MailGetRewardResponse = AscNet.Common.MsgPack.MailGetRewardResponse;
using MailGetSingleRewardRequest = AscNet.Common.MsgPack.MailGetSingleRewardRequest;
using MailGetSingleRewardResponse = AscNet.Common.MsgPack.MailGetSingleRewardResponse;
using MailDeleteRequest = AscNet.Common.MsgPack.MailDeleteRequest;
using MailDeleteResponse = AscNet.Common.MsgPack.MailDeleteResponse;
using MailReadRequest = AscNet.Common.MsgPack.MailReadRequest;
using MailReadResponse = AscNet.Common.MsgPack.MailReadResponse;
using MessagePack;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateVersion46MailCompatibility()
    {
        ValidateMailWireContracts();
        ValidateMailHandlerRegistration();
        ValidateMailTableRewardResolution();
        ValidateNewPlayerWelcomeMail();
        ValidateMailReadDeleteAndExpiryCompatibility();
        ValidateMailRewardClaimCompatibility();
    }

    private static void ValidateMailWireContracts()
    {
        AssertMailNamedMapKeys(new MailReadRequest { Id = "mail-wire-id" },
            ["Id"], "MailReadRequest");
        AssertMailNamedMapKeys(RequiredMailHandlerType("MailGetSingleRewardRequest"),
            new Dictionary<string, object?> { ["Id"] = "mail-wire-id" }, ["Id"], "MailGetSingleRewardRequest");
        AssertMailNamedMapKeys(RequiredMailHandlerType("MailGetRewardRequest"),
            new Dictionary<string, object?> { ["IdList"] = new List<string> { "mail-wire-id" } }, ["IdList"], "MailGetRewardRequest");

        AssertMailNamedMapKeys(new MailReadResponse(), ["Code"], "MailReadResponse");
        AssertMailNamedMapKeys(new MailDeleteResponse(), ["DelIdList"], "MailDeleteResponse");
        AssertMailNamedMapKeys(RequiredMailHandlerType("MailGetSingleRewardResponse"), null,
            ["Code", "RewardGoodsList", "Status"], "MailGetSingleRewardResponse");
        AssertMailNamedMapKeys(RequiredMailHandlerType("MailGetRewardResponse"), null,
            ["Code", "RewardGoodsList", "MailStatus"], "MailGetRewardResponse");

        NotifyLoginMailCollectionBoxData collectionBox = new();
        AssertMailNamedMapKeys(collectionBox,
            ["OpenActivityIds", "MailCollectionBoxDataDb"], "NotifyLoginMailCollectionBoxData");
        AssertMailNamedMapKeys(collectionBox.MailCollectionBoxDataDb,
            ["ReceivedFavoriteMailIds"], "NotifyLoginMailCollectionBoxData.MailCollectionBoxDataDb");
        AssertEqual(0, collectionBox.OpenActivityIds.Count,
            "mail collection-box login starts with no open activities");
        AssertEqual(0, collectionBox.MailCollectionBoxDataDb.ReceivedFavoriteMailIds.Count,
            "mail collection-box login initializes favorite-mail state");

        NotifyMails.NotifyMailsNewMailList mail = new();
        SetMailProperty(mail, "Id", "mail-wire-id");
        SetMailProperty(mail, "GroupId", 1);
        SetMailProperty(mail, "BatchId", null);
        SetMailProperty(mail, "Type", 1);
        SetMailProperty(mail, "Status", 0);
        SetMailProperty(mail, "SendName", "sender");
        SetMailProperty(mail, "Title", "title");
        SetMailProperty(mail, "Content", "content");
        SetMailProperty(mail, "CreateTime", 1L);
        SetMailProperty(mail, "SendTime", 1L);
        SetMailProperty(mail, "ExpireTime", 0L);
        SetMailProperty(mail, "RewardGoodsList", null);
        SetMailProperty(mail, "IsForbidDelete", false);
        SetMailProperty(mail, "IsSurvey", false);
        SetMailProperty(mail, "ReserveTime", 0L);
        AssertMailNamedMapKeys(mail,
            ["Id", "GroupId", "BatchId", "Type", "Status", "SendName", "Title", "Content", "CreateTime", "SendTime", "ExpireTime", "RewardGoodsList", "IsForbidDelete", "IsSurvey", "ReserveTime"],
            "NotifyMails.NewMailList");

        Type goodsType = typeof(MailRewardGoods);
        AssertEqual(null, goodsType.GetProperty("ShowQuality"), "mail reward goods omits shared ShowQuality");
        AssertMailNamedMapKeys(goodsType, null,
            ["RewardType", "TemplateId", "Count", "Level", "Quality", "Grade", "Breakthrough", "ConvertFrom", "IsGift", "RewardMulti", "Id"],
            "NotifyMails reward goods");
    }

    private static void ValidateMailHandlerRegistration()
    {
        foreach (string request in new[] { "MailReadRequest", "MailDeleteRequest", "MailGetSingleRewardRequest", "MailGetRewardRequest" })
        {
            MethodInfo handler = GetRegisteredRequestHandlerMethod(request);
            AssertEqual("AscNet.GameServer.Handlers.MailModule", handler.DeclaringType?.FullName, $"{request} registered MailModule handler");
        }
    }

    private static void ValidateNewPlayerWelcomeMail()
    {
        const long playerId = 46_899;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Type module = GetRegisteredRequestHandlerMethod(nameof(MailReadRequest)).DeclaringType
            ?? throw new InvalidDataException("MailReadRequest handler has no declaring type.");
        MethodInfo ensure = module.GetMethod("EnsureSystemMails", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("MailModule.EnsureSystemMails is missing.");

        AssertEqual(true, (bool)ensure.Invoke(null, [player, now])!,
            "new player receives configured welcome mail");
        PlayerMail welcome = player.Mails.Single();
        AssertEqual(true, welcome.Title.Contains("Welcome to AscNet", StringComparison.Ordinal),
            "new player welcome mail title");
        AssertEqual(true, welcome.IsForbidDelete,
            "new player welcome mail cannot be bulk-deleted");
        AssertEqual(1, BuildMailDelta(player, now).NewMailList.Count,
            "new player login publishes welcome mail");
        AssertEqual(false, (bool)ensure.Invoke(null, [player, now + 1])!,
            "welcome mail is issued only once");
        AssertEqual(1, player.Mails.Count,
            "welcome mail is not duplicated");
    }

    private static void ValidateMailTableRewardResolution()
    {
        List<MailRewardTable> allRewards = TableReaderV2.Parse<MailRewardTable>()
            .Where(row => row.RewardIds.Count > 0)
            .ToList();

        Dictionary<int, MailRewardGoodsTable> goods = TableReaderV2.Parse<MailRewardGoodsTable>()
            .GroupBy(row => row.Id)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        MethodInfo resolve = GetRegisteredRequestHandlerMethod(nameof(MailReadRequest)).DeclaringType!
            .GetMethod("ResolveMailRewards", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("MailModule.ResolveMailRewards is missing.");

        List<MailRewardTable> rewards = allRewards
            .Where(reward => resolve.Invoke(null, [reward.Id]) is List<PlayerMailRewardGoods> { Count: > 0 })
            .Take(2)
            .ToList();
        if (rewards.Count != 2)
            throw new InvalidDataException("mail table coverage requires two resolvable MailReward templates.");

        foreach (MailRewardTable reward in rewards)
        {
            List<PlayerMailRewardGoods> snapshot = (List<PlayerMailRewardGoods>)resolve.Invoke(null, [reward.Id])!;
            AssertEqual(reward.RewardIds.Count, snapshot.Count, $"mail reward {reward.Id} resolved count");
            foreach ((int rewardId, PlayerMailRewardGoods actual) in reward.RewardIds.Zip(snapshot!))
            {
                MailRewardGoodsTable expected = goods[rewardId];
                AssertEqual(expected.TemplateId, (int)actual.TemplateId, $"mail reward {reward.Id}/{rewardId} template");
                AssertEqual(expected.Count, actual.Count, $"mail reward {reward.Id}/{rewardId} count");
                AssertEqual(expected.Id, actual.Id, $"mail reward {reward.Id}/{rewardId} good id");
            }
        }
    }

    private static Type RequiredMailHandlerType(string name) =>
        typeof(MailReadRequest).Assembly.GetType($"AscNet.Common.MsgPack.{name}", throwOnError: true)!;

    private static void ValidateMailReadDeleteAndExpiryCompatibility()
    {
        const long playerId = 46_900;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Player player = CreateDrawCompatibilityPlayer(playerId);
        PlayerMail read = Mail("read", 0, null);
        PlayerMail claimed = Mail("claimed", 3, [new PlayerMailRewardGoods { RewardType = 1, TemplateId = 1, Count = 1 }]);
        PlayerMail protectedMail = Mail("protected", 1, null, forbidDelete: true);
        PlayerMail unclaimed = Mail("unclaimed", 1, [new PlayerMailRewardGoods { RewardType = 1, TemplateId = 1, Count = 1 }]);
        PlayerMail expired = Mail("expired", 1, null, expireTime: now - 1);
        PlayerMail reserved = Mail("reserved", 1, null, forbidDelete: true, expireTime: now - 1, reserveTime: now + 60);
        player.Mails = [read, claimed, protectedMail, unclaimed, expired, reserved];

        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> saves, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "v46-mail-state");

        NotifyMails firstLogin = BuildMailDelta(player, now);
        NotifyMails secondLogin = BuildMailDelta(player, now);
        AssertEqual(Convert.ToHexString(MessagePackSerializer.Serialize(firstLogin)),
            Convert.ToHexString(MessagePackSerializer.Serialize(secondLogin)),
            "duplicate login emits identical state-derived mail delta");
        AssertEqual(true, firstLogin.NewMailList.All(mail => mail.Id is not null),
            "mail delta uses string runtime ids");

        InvokeRegisteredRequestHandler(nameof(MailReadRequest), harness.Session, 46_901,
            new MailReadRequest { Id = read.Id });
        AssertEqual(0, ReadResponsePayload<MailReadResponse>(harness.ReadPacket("mail read response"), nameof(MailReadResponse)).Code,
            "mail read response code");
        AssertEqual(1, read.Status, "mail read marks persisted state");
        AssertEqual(1, saves.ReplaceOneCalls, "mail read saves once");

        InvokeRegisteredRequestHandler(nameof(MailDeleteRequest), harness.Session, 46_902, null);
        MailDeleteResponse deleted = ReadResponsePayload<MailDeleteResponse>(
            harness.ReadPacket("mail delete response"), nameof(MailDeleteResponse));
        AssertMailStrings(["expired", "read", "claimed"], deleted.DelIdList,
            "mail delete returns durable expiry then one-shot deletions");
        AssertEqual(true, player.Mails.All(mail => mail.Id is not "expired" and not "read" and not "claimed"),
            "mail delete removes only eligible records");
        AssertEqual(true, player.Mails.Any(mail => mail.Id == "protected") && player.Mails.Any(mail => mail.Id == "unclaimed"),
            "mail delete preserves protected and unclaimed rewards");
        AssertEqual(true, player.Mails.Any(mail => mail.Id == "reserved"), "reserved expiry remains visible");
        AssertMailStrings(["expired"], player.MailExpireIds, "natural expiry persists reconciliation id");

        Player relog = BsonSerializer.Deserialize<Player>(player.ToBson());
        AssertMailStrings(["expired"], relog.MailExpireIds, "mail expiry survives BSON relog");
        InvokeRegisteredRequestHandler(nameof(MailDeleteRequest), harness.Session, 46_903, null);
        MailDeleteResponse repeated = ReadResponsePayload<MailDeleteResponse>(
            harness.ReadPacket("repeated mail delete response"), nameof(MailDeleteResponse));
        AssertMailStrings(["expired"], repeated.DelIdList, "repeated delete drops one-shot deleted ids");
        AssertEqual(true, saves.ReplaceOneCalls >= 2, "mail deletion saves state");

        static PlayerMail Mail(string id, int status, List<PlayerMailRewardGoods>? rewards, bool forbidDelete = false,
            long expireTime = 0, long reserveTime = 0) => new()
        {
            Id = id, Status = status, RewardGoodsList = rewards, IsForbidDelete = forbidDelete,
            ExpireTime = expireTime, ReserveTime = reserveTime
        };
    }

    private static void AssertMailStrings(IReadOnlyList<string> expected, IReadOnlyList<string>? actual, string name)
    {
        if (actual is null || !expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidDataException($"{name}: expected [{string.Join(',', expected)}], observed [{string.Join(',', actual ?? [])}].");
    }

    private static NotifyMails BuildMailDelta(Player player, long now)
    {
        Type module = GetRegisteredRequestHandlerMethod(nameof(MailReadRequest)).DeclaringType
            ?? throw new InvalidDataException("MailReadRequest handler has no declaring type.");
        MethodInfo build = module.GetMethod("BuildNotifyMails", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("MailModule.BuildNotifyMails is missing.");
        return (NotifyMails)(build.Invoke(null, [player, now])
            ?? throw new InvalidDataException("MailModule.BuildNotifyMails returned null."));
    }


    private static void ValidateMailRewardClaimCompatibility()
    {
        const long playerId = 46_910;
        ItemTable itemTable = TableReaderV2.Parse<ItemTable>().First(item => Inventory.IsValidClientItemId(item.Id));
        int itemId = itemTable.Id;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        PlayerMail single = new()
        {
            Id = "single", RewardGoodsList = [new PlayerMailRewardGoods { RewardType = 1, TemplateId = (uint)itemId, Count = 2 }]
        };
        PlayerMail bulkA = new()
        {
            Id = "bulk-a", RewardGoodsList = [new PlayerMailRewardGoods { RewardType = 1, TemplateId = (uint)itemId, Count = 3 }]
        };
        PlayerMail bulkB = new()
        {
            Id = "bulk-b", RewardGoodsList = [new PlayerMailRewardGoods { RewardType = 1, TemplateId = (uint)itemId, Count = 5 }]
        };
        PlayerMail noReward = new() { Id = "empty" };
        player.Mails = [single, bulkA, bulkB, noReward];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "v46-mail-rewards");

        InvokeRegisteredRequestHandler(nameof(MailGetSingleRewardRequest), harness.Session, 46_911,
            new MailGetSingleRewardRequest { Id = single.Id });
        AssertItemPush(harness.ReadPacket("single reward item push"), itemId, 2, "single reward precedes response");
        MailGetSingleRewardResponse singleResponse = ReadResponsePayload<MailGetSingleRewardResponse>(
            harness.ReadPacket("single reward response"), nameof(MailGetSingleRewardResponse));
        AssertEqual(0, singleResponse.Code, "single reward code");
        AssertEqual(3, single.Status, "single reward marks claimed");
        AssertEqual(2L, harness.Session.inventory.Items.Single(item => item.Id == itemId).Count, "single reward absolute inventory");

        InvokeRegisteredRequestHandler(nameof(MailGetRewardRequest), harness.Session, 46_912,
            new MailGetRewardRequest { IdList = [bulkA.Id, bulkB.Id] });
        AssertEqual(10L, ReadItemPush(harness.ReadPacket("bulk reward item push"), "bulk reward item push")
            .ItemDataList.First(item => item.Id == itemId).Count,
            "bulk reward aggregates inventory before response");
        MailGetRewardResponse bulkResponse = ReadResponsePayload<MailGetRewardResponse>(
            harness.ReadPacket("bulk reward response"), nameof(MailGetRewardResponse));
        AssertEqual(0, bulkResponse.Code, "bulk reward code");
        AssertEqual(3, bulkResponse.MailStatus[bulkA.Id], "bulk reward first status");
        AssertEqual(3, bulkResponse.MailStatus[bulkB.Id], "bulk reward second status");
        AssertEqual(10L, harness.Session.inventory.Items.Single(item => item.Id == itemId).Count, "bulk reward absolute aggregation");

        InvokeRegisteredRequestHandler(nameof(MailGetSingleRewardRequest), harness.Session, 46_913,
            new MailGetSingleRewardRequest { Id = single.Id });
        MailGetSingleRewardResponse repeated = ReadResponsePayload<MailGetSingleRewardResponse>(
            harness.ReadPacket("repeated reward response"), nameof(MailGetSingleRewardResponse));
        AssertEqual(3, repeated.Status, "repeated claim reports claimed status");
        AssertEqual(10L, harness.Session.inventory.Items.Single(item => item.Id == itemId).Count, "repeated claim never duplicates inventory");
        PlayerMail rollback = new()
        {
            Id = "rollback", RewardGoodsList = [new PlayerMailRewardGoods { RewardType = 1, TemplateId = (uint)itemId, Count = 7 }]
        };
        player.Mails.Add(rollback);
        long balanceBeforeRollback = harness.Session.inventory.Items.Single(item => item.Id == itemId).Count;
        playerSaves.ThrowOnReplaceOne = true;
        InvokeRegisteredRequestHandler(nameof(MailGetSingleRewardRequest), harness.Session, 46_916,
            new MailGetSingleRewardRequest { Id = rollback.Id });
        playerSaves.ThrowOnReplaceOne = false;
        MailGetSingleRewardResponse failedRollback = ReadResponsePayload<MailGetSingleRewardResponse>(
            harness.ReadPacket("failed receipt player-save response"), nameof(MailGetSingleRewardResponse));
        AssertEqual(true, failedRollback.Code != 0, "player save failure rejects mail claim after receipt");
        AssertEqual(0, rollback.Status, "player save failure rolls mail status back exactly");
        AssertEqual(balanceBeforeRollback + 7, harness.Session.inventory.Items.Single(item => item.Id == itemId).Count,
            "receipt grant is durable before player save failure");

        InvokeRegisteredRequestHandler(nameof(MailGetSingleRewardRequest), harness.Session, 46_917,
            new MailGetSingleRewardRequest { Id = rollback.Id });
        AssertItemPush(harness.ReadPacket("receipt retry current item push"), itemId, balanceBeforeRollback + 7,
            "receipt retry reports current inventory without a duplicate grant");
        MailGetSingleRewardResponse retryRollback = ReadResponsePayload<MailGetSingleRewardResponse>(
            harness.ReadPacket("receipt retry response"), nameof(MailGetSingleRewardResponse));
        AssertEqual(0, retryRollback.Code, "receipt retry converges");
        AssertEqual(3, rollback.Status, "receipt retry persists claimed status");
        PlayerMail capacity = new()
        {
            Id = "capacity", RewardGoodsList = [new PlayerMailRewardGoods { RewardType = 1, TemplateId = (uint)itemId, Count = 1 }]
        };
        player.Mails.Add(capacity);
        harness.Session.inventory.Items.Single(item => item.Id == itemId).Count = Inventory.GetMaxCount(itemTable);
        InvokeRegisteredRequestHandler(nameof(MailGetSingleRewardRequest), harness.Session, 46_918,
            new MailGetSingleRewardRequest { Id = capacity.Id });
        AssertEqual(true, ReadResponsePayload<MailGetSingleRewardResponse>(
            harness.ReadPacket("capacity reward response"), nameof(MailGetSingleRewardResponse)).Code != 0,
            "full item capacity rejects mail claim");
        AssertEqual(0, capacity.Status, "capacity rejection leaves mail unclaimed");

        InvokeRegisteredRequestHandler(nameof(MailGetSingleRewardRequest), harness.Session, 46_914,
            new MailGetSingleRewardRequest { Id = noReward.Id });
        AssertEqual(true, ReadResponsePayload<MailGetSingleRewardResponse>(
            harness.ReadPacket("empty reward response"), nameof(MailGetSingleRewardResponse)).Code != 0,
            "rewardless mail rejects claim");
        InvokeRegisteredRequestHandler(nameof(MailGetSingleRewardRequest), harness.Session, 46_915,
            new MailGetSingleRewardRequest { Id = "missing" });
        AssertEqual(true, ReadResponsePayload<MailGetSingleRewardResponse>(
            harness.ReadPacket("missing reward response"), nameof(MailGetSingleRewardResponse)).Code != 0,
            "missing mail rejects claim");
        AssertEqual(true, playerSaves.ReplaceOneCalls >= 2, "claims persist player mailbox state");
    }
    private static void SetMailProperty(object target, string name, object? value)
    {
        PropertyInfo property = target.GetType().GetProperty(name)
            ?? throw new InvalidDataException($"{target.GetType().Name} is missing {name}.");
        property.SetValue(target, value);
    }

    private static void AssertMailNamedMapKeys<T>(T value, IReadOnlyCollection<string> expected, string name)
        where T : notnull =>
        AssertMailNamedMapKeys(typeof(T), value, expected, name);

    private static void AssertMailNamedMapKeys(Type type, IReadOnlyDictionary<string, object?>? values, IReadOnlyCollection<string> expected, string name)
    {
        object instance = Activator.CreateInstance(type) ?? throw new InvalidDataException($"{name}: cannot construct {type.Name}.");
        if (values is not null)
            foreach ((string key, object? value) in values)
                SetMailProperty(instance, key, value);
        AssertMailNamedMapKeys(type, instance, expected, name);
    }

    private static void AssertMailNamedMapKeys(Type type, object value, IReadOnlyCollection<string> expected, string name)
    {
        byte[] payload = MessagePackSerializer.Serialize(type, value);
        MessagePackReader reader = new(new ReadOnlySequence<byte>(payload));
        int count = reader.ReadMapHeader();
        HashSet<string> keys = new(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            keys.Add(reader.ReadString() ?? throw new InvalidDataException($"{name}: nil key at {index}."));
            reader.Skip();
        }
        if (!keys.SetEquals(expected))
            throw new InvalidDataException($"{name}: expected [{string.Join(',', expected.Order())}], observed [{string.Join(',', keys.Order())}].");
    }
}
