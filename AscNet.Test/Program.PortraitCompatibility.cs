using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table;
using AscNet.Table.V2.share.exhibition;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.headportrait;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidatePortraitCompatibility()
    {
        AssertMailNamedMapKeys(new NotifyHeadPortraitInfos(), ["Heads"], "NotifyHeadPortraitInfos");
        AssertMailNamedMapKeys(new HeadPortraitList(), ["Id", "LeftCount", "BeginTime"], "HeadPortraitList");

        List<HeadPortraitTable> heads = TableReaderV2.Parse<HeadPortraitTable>();
        MethodInfo getRewardType = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler"),
            "GetRewardType", BindingFlags.Static | BindingFlags.Public, [typeof(RewardGoodsTable)]);
        RewardGoodsTable portraitReward = FindHeadReward(1, "portrait");
        RewardGoodsTable frameReward = FindHeadReward(2, "frame");

        ValidateRewardGoodsHeadGrant(portraitReward, "portrait");
        ValidateRewardGoodsHeadGrant(frameReward, "frame");
        ValidateFashionGiftRepair(heads);
        ValidateClaimedExhibitionPortraitLogin(heads, getRewardType);
        ValidateAtomicPortraitRewards(portraitReward, heads, getRewardType);
        ValidateMalformedPortraitRewards();

        RewardGoodsTable FindHeadReward(int type, string name)
        {
            return TableReaderV2.Parse<RewardGoodsTable>()
                .OrderBy(row => row.Id)
                .FirstOrDefault(row => getRewardType.Invoke(null, [row]) is RewardType.HeadPortrait
                    && heads.Any(head => head.Id == row.TemplateId && head.Type == type))
                ?? throw new InvalidDataException($"portrait compatibility: no table-derived Type={type} {name} RewardGoods row.");
        }
    }

    private static void ValidateRewardGoodsHeadGrant(RewardGoodsTable reward, string name)
    {
        long playerId = 46_970 + reward.Id;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> saves, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), $"portrait-compat-{name}");
        MethodInfo giveRewards = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler"),
            "GiveRewards", BindingFlags.Static | BindingFlags.Public,
            [typeof(IEnumerable<RewardGoodsTable>), typeof(Session)]);

        giveRewards.Invoke(null, [new[] { reward }, harness.Session]);
        NotifyHeadPortraitInfos push = ReadPushPayload<NotifyHeadPortraitInfos>(harness,
            nameof(NotifyHeadPortraitInfos), $"{name} RewardGoods portrait push");
        AssertIntegerList([reward.TemplateId], push.Heads.Select(head => head.Id).ToArray(),
            $"{name} RewardGoods exact Heads");
        AssertIntegerList([reward.TemplateId], player.HeadPortraits.Select(head => head.Id).ToArray(),
            $"{name} RewardGoods stored heads");
        AssertEqual(1, saves.ReplaceOneCalls, $"{name} RewardGoods saves player once");
        Player persisted = BsonSerializer.Deserialize<Player>((saves.LastReplacement
            ?? throw new InvalidDataException($"{name} RewardGoods did not persist player.")).ToBson());
        AssertIntegerList([reward.TemplateId], persisted.HeadPortraits.Select(head => head.Id).ToArray(),
            $"{name} RewardGoods BSON heads");

        giveRewards.Invoke(null, [new[] { reward }, harness.Session]);
        AssertEqual(1, saves.ReplaceOneCalls, $"duplicate {name} RewardGoods does not save");
        AssertNoAvailablePacket(harness, $"duplicate {name} RewardGoods");
    }

    private static void ValidateFashionGiftRepair(IReadOnlyCollection<HeadPortraitTable> heads)
    {
        FashionTable fashion = TableReaderV2.Parse<FashionTable>()
            .OrderBy(row => row.Id)
            .FirstOrDefault(row => row.GiftId is > 0 && heads.Any(head => head.Id == row.GiftId.Value))
            ?? throw new InvalidDataException("portrait compatibility: no FashionTable GiftId backed by HeadPortraitTable.");
        long playerId = 46_980 + fashion.Id;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Character character = CreateDrawCompatibilityCharacter(playerId);
        character.Fashions.Add(new FashionList { Id = fashion.Id, IsLock = false });
        using LoopbackSessionHarness harness = new(character, player, CreateDrawCompatibilityInventory(playerId, []),
            "portrait-compat-fashion-gift");
        MethodInfo unlockFashion = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler"),
            "UnlockFashionReward", BindingFlags.Static | BindingFlags.Public,
            [typeof(int), typeof(Session), typeof(List<FashionList>), typeof(List<HeadPortraitList>)]);

        List<FashionList> fashionDelta = [];
        List<HeadPortraitList> headDelta = [];
        bool changed = (bool)unlockFashion.Invoke(null, [fashion.Id, harness.Session, fashionDelta, headDelta])!;
        AssertEqual(false, changed, "owned Fashion GiftId repair does not re-add fashion");
        AssertEqual(1, character.Fashions.Count, "owned Fashion GiftId repair keeps one fashion");
        AssertEqual(0, fashionDelta.Count, "owned Fashion GiftId repair emits no fashion delta");
        AssertIntegerList([fashion.GiftId!.Value], headDelta.Select(head => head.Id).ToArray(),
            "owned Fashion GiftId repair emits exact portrait delta");
        AssertIntegerList([fashion.GiftId.Value], player.HeadPortraits.Select(head => head.Id).ToArray(),
            "owned Fashion GiftId repair unlocks gift");

        fashionDelta = [];
        headDelta = [];
        changed = (bool)unlockFashion.Invoke(null, [fashion.Id, harness.Session, fashionDelta, headDelta])!;
        AssertEqual(false, changed, "duplicate Fashion GiftId repair unchanged");
        AssertEqual(1, character.Fashions.Count, "duplicate Fashion GiftId repair keeps one fashion");
        AssertEqual(0, fashionDelta.Count, "duplicate Fashion GiftId repair emits no fashion delta");
        AssertEqual(0, headDelta.Count, "duplicate Fashion GiftId repair emits no portrait delta");
    }

    private static void ValidateClaimedExhibitionPortraitLogin(
        IReadOnlyCollection<HeadPortraitTable> heads,
        MethodInfo getRewardType)
    {
        List<RewardGoodsTable> goods = TableReaderV2.Parse<RewardGoodsTable>();
        (ExhibitionRewardTable Exhibition, RewardGoodsTable Reward) selected = TableReaderV2.Parse<ExhibitionRewardTable>()
            .Where(row => row.RewardId is > 0)
            .OrderBy(row => row.Id)
            .SelectMany(row => ResolveRewardGoods(row.RewardId!.Value, goods, "portrait compatibility exhibition")
                .Where(reward => getRewardType.Invoke(null, [reward]) is RewardType.HeadPortrait
                    && heads.Any(head => head.Id == reward.TemplateId))
                .Select(reward => (row, reward)))
            .FirstOrDefault();
        if (selected.Exhibition is null || selected.Reward is null)
            throw new InvalidDataException("portrait compatibility: no ExhibitionReward bundle contains a catalog portrait reward.");

        long playerId = 46_990 + selected.Exhibition.Id;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.GatherRewards.Add(selected.Exhibition.Id);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> saves, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "portrait-compat-exhibition-login");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        MethodInfo doLogin = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "DoLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(bool)]);

        doLogin.Invoke(null, [harness.Session, false]);
        NotifyLogin login = ReadPushPayload<NotifyLogin>(harness, nameof(NotifyLogin),
            "claimed ExhibitionReward NotifyLogin");
        if (!login.HeadPortraitList.Any(head => head.Id == selected.Reward.TemplateId))
            throw new InvalidDataException("claimed ExhibitionReward portrait is absent from NotifyLogin.");
        Player persisted = BsonSerializer.Deserialize<Player>((saves.LastReplacement
            ?? throw new InvalidDataException("claimed ExhibitionReward login did not persist player.")).ToBson());
        if (!persisted.HeadPortraits.Any(head => head.Id == selected.Reward.TemplateId))
            throw new InvalidDataException("claimed ExhibitionReward login persisted player without repaired portrait.");
    }

    private static void ValidateAtomicPortraitRewards(
        RewardGoodsTable portraitReward,
        IReadOnlyCollection<HeadPortraitTable> heads,
        MethodInfo getRewardType)
    {
        Type rewardHandler = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
        Type rewardGrant = rewardHandler.Assembly.GetType("AscNet.GameServer.Handlers.RewardGrant", throwOnError: true)!;
        MethodInfo applyOnce = rewardHandler.GetMethod("ApplyRewardsOnceAndPersist",
            BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException(rewardHandler.FullName, "ApplyRewardsOnceAndPersist");
        const string portraitClaim = "portrait-compat:atomic-head";
        long playerId = 47_010 + portraitReward.Id;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        Character character = CreateDrawCompatibilityCharacter(playerId);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _);
        using LoopbackSessionHarness harness = new(character, player, CreateDrawCompatibilityInventory(playerId, []),
            "portrait-compat-atomic-head");

        object result = ApplyAtomic(portraitClaim, portraitReward);
        SendAtomicPushes(result, harness);
        AssertIntegerList([portraitReward.TemplateId],
            ReadPushPayload<NotifyHeadPortraitInfos>(harness, nameof(NotifyHeadPortraitInfos),
                "atomic portrait first push").Heads.Select(head => head.Id).ToArray(),
            "atomic portrait first exact Heads");
        AssertEqual(1, playerSaves.ReplaceOneCalls, "atomic portrait first player save");
        AssertEqual(true, character.AppliedRewardClaims.Contains(portraitClaim, StringComparer.Ordinal),
            "atomic portrait first character receipt");

        result = ApplyAtomic(portraitClaim, portraitReward);
        SendAtomicPushes(result, harness);
        AssertIntegerList([portraitReward.TemplateId],
            ReadPushPayload<NotifyHeadPortraitInfos>(harness, nameof(NotifyHeadPortraitInfos),
                "atomic portrait retry current-state push").Heads.Select(head => head.Id).ToArray(),
            "atomic portrait retry exact current-state Heads");
        AssertEqual(1, player.HeadPortraits.Count, "atomic portrait retry does not duplicate head");
        AssertEqual(1, playerSaves.ReplaceOneCalls, "atomic portrait retry does not persist player");

        const string failedClaim = "portrait-compat:atomic-head-save-failure";
        Player failedPlayer = CreateDrawCompatibilityPlayer(playerId + 1);
        Character failedCharacter = CreateDrawCompatibilityCharacter(playerId + 1);
        using MongoCollectionOverride failedMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> failedSaves, out _, out _);
        using LoopbackSessionHarness failedHarness = new(failedCharacter, failedPlayer,
            CreateDrawCompatibilityInventory(playerId + 1, []), "portrait-compat-atomic-head-failure");
        failedSaves.ThrowOnReplaceOne = true;
        try
        {
            _ = ApplyAtomic(failedClaim, portraitReward, failedHarness.Session);
            throw new InvalidDataException("atomic portrait forced Player.SaveChecked failure did not throw.");
        }
        catch (TargetInvocationException)
        {
        }
        AssertEqual(0, failedPlayer.HeadPortraits.Count,
            "atomic portrait Player.SaveChecked failure restores in-memory heads");
        AssertEqual(true, failedCharacter.AppliedRewardClaims.Contains(failedClaim, StringComparer.Ordinal),
            "atomic portrait Player.SaveChecked failure retains character receipt");

        failedSaves.ThrowOnReplaceOne = false;
        result = ApplyAtomic(failedClaim, portraitReward, failedHarness.Session);
        SendAtomicPushes(result, failedHarness);
        AssertIntegerList([portraitReward.TemplateId],
            ReadPushPayload<NotifyHeadPortraitInfos>(failedHarness, nameof(NotifyHeadPortraitInfos),
                "atomic portrait failure retry current-state push").Heads.Select(head => head.Id).ToArray(),
            "atomic portrait failure retry exact Heads");
        AssertIntegerList([portraitReward.TemplateId], failedPlayer.HeadPortraits.Select(head => head.Id).ToArray(),
            "atomic portrait failure retry converges heads");
        AssertEqual(true, failedCharacter.AppliedRewardClaims.Contains(failedClaim, StringComparer.Ordinal),
            "atomic portrait failure retry uses character receipt");

        RewardGoodsTable fashionReward = TableReaderV2.Parse<RewardGoodsTable>()
            .OrderBy(row => row.Id)
            .FirstOrDefault(row => getRewardType.Invoke(null, [row]) is RewardType.Fashion
                && TableReaderV2.Parse<FashionTable>().Any(fashion => fashion.Id == row.TemplateId
                    && fashion.GiftId is > 0 && heads.Any(head => head.Id == fashion.GiftId.Value)))
            ?? throw new InvalidDataException("portrait compatibility: no atomic Fashion RewardGoods with a catalog GiftId.");
        FashionTable fashion = TableReaderV2.Parse<FashionTable>().Single(row => row.Id == fashionReward.TemplateId);
        long fashionPlayerId = playerId + 2;
        Player fashionPlayer = CreateDrawCompatibilityPlayer(fashionPlayerId);
        Character fashionCharacter = CreateDrawCompatibilityCharacter(fashionPlayerId);
        using MongoCollectionOverride fashionMongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> fashionSaves, out _, out _);
        using LoopbackSessionHarness fashionHarness = new(fashionCharacter, fashionPlayer,
            CreateDrawCompatibilityInventory(fashionPlayerId, []), "portrait-compat-atomic-fashion");
        result = ApplyAtomic("portrait-compat:atomic-fashion", fashionReward, fashionHarness.Session);
        SendAtomicPushes(result, fashionHarness);
        FashionSyncNotify fashionPush = ReadPushPayload<FashionSyncNotify>(fashionHarness, nameof(FashionSyncNotify),
            "atomic Fashion GiftId fashion push");
        AssertIntegerList([fashion.Id], fashionPush.FashionList.Select(entry => entry.Id).ToArray(),
            "atomic Fashion GiftId exact fashion delta");
        AssertIntegerList([fashion.GiftId!.Value],
            ReadPushPayload<NotifyHeadPortraitInfos>(fashionHarness, nameof(NotifyHeadPortraitInfos),
                "atomic Fashion GiftId portrait push").Heads.Select(head => head.Id).ToArray(),
            "atomic Fashion GiftId exact Heads");
        AssertEqual(1, fashionSaves.ReplaceOneCalls, "atomic Fashion GiftId saves player");

        object ApplyAtomic(string claimKey, RewardGoodsTable reward, Session? targetSession = null)
        {
            Array grants = Array.CreateInstance(rewardGrant, 1);
            grants.SetValue(Activator.CreateInstance(rewardGrant, claimKey, new[] { reward }), 0);
            return applyOnce.Invoke(null, [grants, targetSession ?? harness.Session])
                ?? throw new InvalidDataException($"atomic reward {claimKey} returned null.");
        }

        static void SendAtomicPushes(object application, LoopbackSessionHarness? targetHarness = null)
        {
            Session session = targetHarness?.Session
                ?? throw new InvalidOperationException("Atomic push sender requires its loopback harness.");
            application.GetType().GetMethod("SendPushes", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(application, [session]);
        }
    }

    private static void ValidateMalformedPortraitRewards()
    {
        const long playerId = 46_999;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> saves, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
            CreateDrawCompatibilityInventory(playerId, []), "portrait-compat-malformed");
        MethodInfo giveRewards = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler"),
            "GiveRewards", BindingFlags.Static | BindingFlags.Public,
            [typeof(IEnumerable<Reward>), typeof(Session)]);

        giveRewards.Invoke(null,
        [
            new Reward[]
            {
                new() { Id = 0, Type = RewardType.HeadPortrait },
                new() { Id = int.MaxValue, Type = RewardType.HeadPortrait }
            },
            harness.Session
        ]);
        AssertEqual(0, player.HeadPortraits.Count, "malformed portrait IDs do not mutate player");
        AssertEqual(0, saves.ReplaceOneCalls, "malformed portrait IDs do not persist player");
        AssertNoAvailablePacket(harness, "malformed portrait IDs");
    }

    private static void AssertNoAvailablePacket(LoopbackSessionHarness harness, string name)
    {
        if (harness.TryReadAvailablePacket(name, out Packet packet))
            throw new InvalidDataException($"{name}: unexpected {packet.Type} packet.");
    }
}
