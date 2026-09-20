using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.share.theatre4;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Character = AscNet.Common.Database.Character;
using Inventory = AscNet.Common.Database.Inventory;

namespace AscNet.Test;

// Awakening Tundra (Theatre4) end-to-end compatibility. Everything is driven through the real
// registered packet handlers over LoopbackSessionHarness and asserted against durable BSON state
// and real pushes, never source text or wiring. Numbers that are not authored tables are only
// asserted against values that came back out of persisted run state. Fixture edits (item/effect/
// colour seeding) are explicit and labelled so they cannot masquerade as gameplay.
internal partial class Program
{
    // EN XEnumConst.Theatre4 GridType/State/AssetType (module is internal, mirrored here).
    private const int T4GridNothing = 1, T4GridEmpty = 2, T4GridShop = 4, T4GridBox = 5,
        T4GridMonster = 6, T4GridBoss = 7, T4GridEvent = 8, T4GridStart = 9, T4GridBlank = 10, T4GridBuilding = 11;
    private const int T4StateUnknown = 0, T4StateVisible = 1, T4StateDiscover = 2, T4StateExplored = 3, T4StateProcessed = 4;
    private const int T4AssetItem = 2, T4AssetGold = 4, T4AssetColorPoint = 9,
        T4AssetColorDailyResource = 12, T4AssetBuildPoint = 10, T4AssetActionPoint = 11,
        T4AssetTimeBack = 17;

    private static readonly HashSet<string> Theatre4Calls = [];

    private static void ValidateTheatre4Compatibility()
    {
        PacketFactory.LoadPacketHandlers();
        Theatre4Calls.Clear();
        // Registration is checked before referencing the provider: an old server must fail for the
        // missing protocol, not a reflection detail.
        _ = GetRegisteredRequestHandler(nameof(Theatre4EnterRequest));
        ValidateTheatre4LifecycleCompatibility();
        ValidateTheatre4MetaCompatibility();
        ValidateTheatre4MapCompatibility();
        ValidateTheatre4FightGroupIdentityCompatibility();
        ValidateTheatre4BoxRecoveryCompatibility();
        ValidateTheatre4EconomyCompatibility();
        ValidateTheatre4EffectCompatibility();
        ValidateTheatre4GridEventCompatibility();
        ValidateTheatre4EventOptionEffectCompatibility();
        ValidateTheatre4CrossMapShopEntryCompatibility();
        ValidateTheatre4CombatCompatibility();
        ValidateTheatre4CompleteRunCompatibility();
        ValidateTheatre4SignedCountdownCompatibility();
        ValidateTheatre4ContinuationCompatibility();
        ValidateTheatre4DevelopmentCompatibility();
        ValidateTheatre4ShopDiscoveryCompatibility();
        ValidateTheatre4CharacterStarCompatibility();
        ValidateTheatre4FightSettlementJournalCompatibility();
        ValidateTheatre4EffectAuditCompatibility();
        ValidateTheatre4ProgressionAuditCompatibility();
        ValidateTheatre4CombatAuditCompatibility();
        ValidateTheatre4BuildingAuditCompatibility();
        ValidateTheatre4TalentAuditCompatibility();

        string[] requests = typeof(Theatre4EnterRequest).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(Theatre4EnterRequest).Namespace
                && type.Name.StartsWith("Theatre4", StringComparison.Ordinal)
                && type.Name.EndsWith("Request", StringComparison.Ordinal))
            .Select(type => type.Name).Order().ToArray();
        AssertEqual("", string.Join(",", requests.Where(name => !Theatre4Calls.Contains(name))),
            "Every Theatre4 RPC receives a correlated protocol response");
        Console.WriteLine($"Theatre4 compatibility: {requests.Length} RPCs, two table-derived routes, "
            + "complete winning run, frozen maps/encounters, durable reward recovery, "
            + "economy/effect/combat boundary rejections passed.");
    }

    //region lifecycle

    private static void ValidateTheatre4LifecycleCompatibility()
    {
        using Theatre4Case test = new("lifecycle");
        NotifyTheatre4ActivityData login = test.Login();
        AssertEqual(true, login.Data.ActivityId > 0, "Theatre4 login adopts an open authored activity");
        AssertEqual(true, login.Data.AdventureData is null, "Theatre4 login starts with no adventure");

        test.Reject(nameof(Theatre4EnterRequest), null, "Enter without an adventure");
        test.Reject(nameof(Theatre4SelectDifficultRequest), new Theatre4SelectDifficultRequest { Difficult = int.MaxValue },
            "Unauthored difficulty");
        test.Reject(nameof(Theatre4SelectDifficultRequest), new Theatre4SelectDifficultRequest { Difficult = 0 },
            "Zero difficulty");

        int beginner = Theatre4DifficultyRows().OrderBy(row => row.Id).First().Id;
        AssertEqual(1, beginner, "Authored difficulty 1 is the beginner route");
        test.StartRun(beginner);
        AssertEqual(beginner, test.Adventure.Difficulty, "Selected difficulty is durable");
        AssertEqual(1, test.Adventure.MapBlueprintId, "Beginner difficulty uses the authored beginner route");
        AssertEqual(1, test.Adventure.Chapters.Count, "Beginner route opens one chapter");
        AssertEqual(11001, test.Adventure.Chapters[0].MapGroup, "Beginner route first MapGroup is authored 11001");
        AssertEqual(true, test.Adventure.Chapters[0].MapId > 0, "Chapter resolves an authored map id");
        AssertEqual(1, test.Adventure.Chapters[0].Grids
            .Count(grid => grid.State == T4StateExplored && grid.Type == T4GridStart), "Exactly one start tile is explored");
        AssertEqual(true, test.Adventure.Chapters[0].Grids.Count(grid => grid.State == T4StateDiscover) > 0,
            "Start tile reveals explorable neighbours");

        test.Call(nameof(Theatre4EnterRequest));
        test.Reject(nameof(Theatre4SelectInheritRequest), new Theatre4SelectInheritRequest { ItemId = 1 },
            "Unoffered inherit item");
        test.Reject(nameof(Theatre4SelectDifficultRequest), new Theatre4SelectDifficultRequest { Difficult = beginner },
            "Cannot reset an active adventure");

        int mapId = test.Adventure.Chapters[0].MapId;
        byte[] grids = MessagePackSerializer.Serialize(test.Adventure.Chapters[0].Grids);
        long mutationsBefore = test.State.NextMutationId;
        test.Relog("offered beginner map");
        AssertEqual(true, grids.SequenceEqual(MessagePackSerializer.Serialize(
            test.Adventure.Chapters[0].Grids)), "Login preserves the offered beginner map byte-for-byte");
        AssertEqual(true, test.State.NextMutationId >= mutationsBefore, "Login never rewinds the mutation journal");
        AssertEqual(mapId, test.Adventure.Chapters[0].MapId, "Login preserves the chapter identity");

        test.Call(nameof(Theatre4SettleAdventureRequest));
        AssertEqual(true, test.Data.AdventureData is null, "Abandon closes the adventure");
        AssertEqual(3, test.Data.PreAdventureSettleData!.SettleType, "Client abandon settles with the abandon type");
        NotifyTheatre4AdventureSettle abandon = test.EndingPush("abandon");
        AssertEndingShape(abandon, 3, beginner, "Abandon ending");
        // A retransmitted manual settle has no active run; its rejection must not erase the ending.
        test.Reject(nameof(Theatre4SettleAdventureRequest), null, "Settling an inactive adventure");
        AssertEqual(true, test.State.PendingSettleAdventure is not null, "Rejected repeat keeps the pending ending");

        NotifyTheatre4AdventureSettle recovered = test.RelogEnding("abandoned run");
        AssertEndingShape(recovered, 3, beginner, "Abandon relog ending");
        AssertEqual(true, MessagePackSerializer.Serialize(abandon.SettleData).SequenceEqual(
            MessagePackSerializer.Serialize(recovered.SettleData)), "Relog recovery re-delivers the same settle summary");
        AssertEqual(true, MessagePackSerializer.Serialize(abandon.AdventureData).SequenceEqual(
            MessagePackSerializer.Serialize(recovered.AdventureData)), "Relog recovery re-delivers the same terminal snapshot");

        List<Packet.Push> restartPushes = [];
        test.StartRun(beginner, restartPushes);
        AssertEqual(beginner, test.Adventure.Difficulty, "Restarted run keeps the selected difficulty");
        AssertEqual(1, test.Adventure.Chapters.Count, "Restarted run opens exactly one fresh chapter");
        AssertEqual(true, test.Adventure.Ap > 0 && test.Adventure.Gold > 0, "Restarted run reseeds authored resources");
        AssertEqual(true, test.State.PendingSettleAdventure is null, "Starting the next run clears the pending ending");
        int clearIndex = restartPushes.FindIndex(push => push.Name == nameof(NotifyTheatre4AdventureSettle));
        int liveIndex = restartPushes.FindIndex(push => push.Name == nameof(NotifyTheatre4AdventureData));
        AssertEqual(true, clearIndex >= 0 && liveIndex > clearIndex,
            "Restart after terminal recovery clears the old ending before publishing the live adventure");
        NotifyTheatre4AdventureSettle restartClear = MessagePackSerializer.Deserialize<NotifyTheatre4AdventureSettle>(
            restartPushes[clearIndex].Content);
        AssertEqual(null, restartClear.AdventureData,
            "Restart terminal clear carries no stale adventure snapshot");
        AssertEqual(true, MessagePackSerializer.Serialize(abandon.SettleData).SequenceEqual(
            MessagePackSerializer.Serialize(restartClear.SettleData)),
            "Restart terminal clear retains the prior settle summary");
        test.PublishRecoveredEnding();
        AssertEqual(false, test.Harness.TryReadAvailablePacket("acknowledged ending", out _),
            "Acknowledged ending is never re-delivered");
    }

    //endregion

    //region account meta (battle pass, tech, save-fault recovery)

    private static void ValidateTheatre4MetaCompatibility()
    {
        using Theatre4Case test = new("meta");
        List<Theatre4BattlePassTable> levels = TableReaderV2.Parse<Theatre4BattlePassTable>()
            .OrderBy(row => row.Level).ToList();
        AssertEqual(true, levels.Count > 1, "Authored battle pass has multiple levels");

        test.StartRun(1);
        test.Data.TotalBattlePassExp = Math.Max(0, levels[0].NeedExp - 1);
        test.SaveFixture();
        test.Reject(nameof(Theatre4BattlePassGetRewardRequest),
            new Theatre4BattlePassGetRewardRequest { GetRewardType = 1, Id = levels[0].Level },
            "Battle pass below threshold");

        test.Data.TotalBattlePassExp = levels.Sum(row => row.NeedExp);
        test.SaveFixture();
        Dictionary<int, long> before = test.ItemCounts();
        JObject single = test.Call(nameof(Theatre4BattlePassGetRewardRequest),
            new Theatre4BattlePassGetRewardRequest { GetRewardType = 1, Id = levels[0].Level });
        AssertIntegerList(new long[] { levels[0].Level },
            RequiredValue<JArray>(single, "GotRewardIds", JTokenType.Array, "Battle pass single")
                .Select(value => value.Value<long>()).ToArray(), "Single claim reports the requested level");
        Theatre4AssertItemRewards(test, before, [levels[0].RewardId], "Battle pass single");
        AssertEqual(true, test.Data.BattlePassGotRewardIds.Contains(levels[0].Level), "Single claim is recorded");
        test.RepeatRejected(nameof(Theatre4BattlePassGetRewardRequest),
            new Theatre4BattlePassGetRewardRequest { GetRewardType = 1, Id = levels[0].Level }, "Battle pass single claim");
        test.Reject(nameof(Theatre4BattlePassGetRewardRequest),
            new Theatre4BattlePassGetRewardRequest { GetRewardType = 9, Id = levels[0].Level },
            "Invalid battle pass selector");

        before = test.ItemCounts();
        JObject all = test.Call(nameof(Theatre4BattlePassGetRewardRequest),
            new Theatre4BattlePassGetRewardRequest { GetRewardType = 2 });
        AssertEqual(levels.Count - 1,
            RequiredValue<JArray>(all, "GotRewardIds", JTokenType.Array, "Battle pass claim-all").Count,
            "Claim-all reports every unclaimed level once");
        Theatre4AssertItemRewards(test, before, levels.Where(row => row.Level != levels[0].Level)
            .Select(row => row.RewardId), "Battle pass claim-all");
        AssertEqual(levels.Count, test.Data.BattlePassGotRewardIds.Distinct().Count(),
            "Every level is claimed exactly once");
        test.Reject(nameof(Theatre4BattlePassGetRewardRequest),
            new Theatre4BattlePassGetRewardRequest { GetRewardType = 2 }, "Exhausted battle pass claim-all");

        Theatre4TechTable tech = TableReaderV2.Parse<Theatre4TechTable>()
            .First(row => !(row.Condition > 0) && row.PreIds.All(id => id <= 0) && row.Cost > 0);
        test.Reject(nameof(Theatre4TechUnlockRequest), new Theatre4TechUnlockRequest { TechId = int.MaxValue },
            "Unauthored tech");
        test.EnsureTechCoin(tech.Cost - 1);
        test.Reject(nameof(Theatre4TechUnlockRequest), new Theatre4TechUnlockRequest { TechId = tech.Id },
            "Tech cannot overdraft the account coin");
        test.EnsureTechCoin(tech.Cost);
        test.Call(nameof(Theatre4TechUnlockRequest), new Theatre4TechUnlockRequest { TechId = tech.Id });
        AssertEqual(true, test.Data.Techs.Contains(tech.Id), "Tech unlock is durable");
        AssertEqual(0L, test.Balance(96200), "Tech charges the exact authored cost");
        test.Reject(nameof(Theatre4TechUnlockRequest), new Theatre4TechUnlockRequest { TechId = tech.Id },
            "Tech cannot be bought twice");

        // Save-fault recovery: a failed reward commit leaves a durable intent that login finalizes
        // exactly once, and the claim can never be replayed afterwards.
        using Theatre4Case recovery = new("reward-recovery");
        recovery.StartRun(1);
        recovery.Data.TotalBattlePassExp = levels.Sum(row => row.NeedExp);
        recovery.SaveFixture();
        Dictionary<int, long> recoveryBefore = recovery.ItemCounts();
        recovery.Inventories.ThrowOnReplaceOne = true;
        try
        {
            recovery.Call(nameof(Theatre4BattlePassGetRewardRequest),
                new Theatre4BattlePassGetRewardRequest { GetRewardType = 2 }, success: false);
        }
        finally
        {
            recovery.Inventories.ThrowOnReplaceOne = false;
        }
        AssertEqual(true, recovery.State.PendingMutation is not null,
            "Failed reward commit leaves a durable recovery intent");
        recovery.Relog("reward recovery", pending: true);
        AssertEqual(true, recovery.State.PendingMutation is null, "Login finishes the durable recovery");
        AssertEqual(levels.Count, recovery.Data.BattlePassGotRewardIds.Distinct().Count(),
            "Recovered claim finalizes every level once");
        Theatre4AssertItemRewards(recovery, recoveryBefore, levels.Select(row => row.RewardId),
            "Battle pass recovered claim");
        recovery.Reject(nameof(Theatre4BattlePassGetRewardRequest),
            new Theatre4BattlePassGetRewardRequest { GetRewardType = 2 }, "Recovered claim cannot replay");
        recovery.Relog("reward recovery complete");
    }

    //endregion

    //region map

    private static void ValidateTheatre4MapCompatibility()
    {
        using Theatre4Case test = new("map");
        test.StartRun(1);
        int mapId = test.Adventure.Chapters[0].MapId;

        test.Reject(nameof(Theatre4ExploreGridRequest),
            new Theatre4ExploreGridRequest { MapId = int.MaxValue, PosX = 0, PosY = 0 }, "Unknown chapter");
        test.Reject(nameof(Theatre4ExploreGridRequest),
            new Theatre4ExploreGridRequest { MapId = mapId, PosX = int.MaxValue, PosY = 0 },
            "Grid outside the authored map");
        test.Reject(nameof(Theatre4ExploreGridRequest),
            new Theatre4ExploreGridRequest { MapId = mapId, PosX = 0, PosY = 0 },
            "Start tile is already explored");

        Theatre4GridData target = test.Adventure.Chapters[0].Grids.First(grid => grid.State == T4StateDiscover);
        int targetX = target.PosX, targetY = target.PosY;
        int apBefore = test.Asset(T4AssetActionPoint, 0);
        JObject explored = test.Call(nameof(Theatre4ExploreGridRequest),
            new Theatre4ExploreGridRequest { MapId = mapId, PosX = targetX, PosY = targetY });
        AssertEqual(targetX, explored["Grid"]!.Value<int>("PosX"), "Explore response returns the selected tile");
        AssertEqual(T4StateExplored, test.Grid(mapId, targetX, targetY).State, "Explored tile advances state");
        AssertEqual(apBefore - 1, test.Asset(T4AssetActionPoint, 0), "Exploration charges the authored action point");
        AssertEqual(true, test.Pushes.Any(push => push.Name == nameof(NotifyTheatre4ChangeGrids)),
            "Exploration publishes the revealed fog");
        test.RepeatRejected(nameof(Theatre4ExploreGridRequest),
            new Theatre4ExploreGridRequest { MapId = mapId, PosX = targetX, PosY = targetY }, "Explored tile");

        test.Adventure.Ap = 0;
        test.SaveFixture();
        JObject daily = test.Call(nameof(Theatre4DailySettleRequest));
        AssertEqual(true, RequiredObject(daily, "SettleResult", "Theatre4 daily settle").Count > 0,
            "Daily settlement returns a summary");
        AssertEqual(true, test.Adventure.Ap > 0, "Daily settlement refills action points");
        AssertEqual(1, test.Adventure.Days, "Daily settlement advances the day exactly once");
        AssertEqual(true, test.Pushes.Any(push => push.Name == nameof(NotifyTheatre4TracebackInfo)),
            "Daily settlement publishes the timeback snapshot");
        byte[] frozen = MessagePackSerializer.Serialize(test.Adventure.Chapters[0].Grids);
        test.Relog("daily settled map");
        AssertEqual(true, frozen.SequenceEqual(MessagePackSerializer.Serialize(test.Adventure.Chapters[0].Grids)),
            "Relog preserves the explored map after a day rollover");

        Theatre4GridData box = Theatre4GridOfType(test, T4GridBox);
        List<Theatre4BoxGroupTable> boxRows = TableReaderV2.Parse<Theatre4BoxGroupTable>();
        _ = boxRows.Single(row => row.Id == box.ContentId && row.GroupId == box.ContentGroup);
        AssertEqual(true, test.Adventure.CreatedBoxGroupIds.Contains(box.ContentGroup),
            "Generated box group is recorded in the run ledger");
        List<Theatre4RewardDropTable> boxDrops = TableReaderV2.Parse<Theatre4RewardDropTable>();
        List<Theatre4RewardTable> boxRewards = TableReaderV2.Parse<Theatre4RewardTable>();
        Theatre4BoxGroupTable boxRow = boxRows.First(row =>
        {
            Theatre4RewardDropTable? drop = boxDrops.FirstOrDefault(candidate => candidate.Id == row.DropId);
            return row.Id != row.GroupId && row.Id != row.DropId && row.GroupId != row.DropId
                && drop is { Type: 1 } && drop.GroupIds.Count > 0
                && drop.Probabilitys.Count >= drop.GroupIds.Count
                && Enumerable.Range(0, drop.GroupIds.Count).All(index => drop.Probabilitys[index] >= 1)
                && boxRewards.Count(reward => drop.GroupIds.Contains(reward.GroupId)
                    && reward.ElementType == T4AssetGold) == 1;
        });
        Theatre4RewardDropTable boxDrop = boxDrops.Single(row => row.Id == boxRow.DropId);
        Theatre4RewardTable boxGold = boxRewards.Single(row =>
            boxDrop.GroupIds.Contains(row.GroupId) && row.ElementType == T4AssetGold);
        box.ContentId = boxRow.Id;
        box.ContentGroup = boxRow.GroupId;
        test.SaveFixture();
        int boxGoldBefore = test.Adventure.Gold;
        test.ExploreToward(mapId, box);
        AssertEqual(boxGold.ElementCount, test.Adventure.Gold - boxGoldBefore,
            "Opening authored box grants its deterministic DropId gold");
        AssertEqual(T4StateProcessed, test.Grid(mapId, box.PosX, box.PosY).State, "Opened box tile lands processed");
        test.RepeatRejected(nameof(Theatre4ExploreGridRequest),
            new Theatre4ExploreGridRequest { MapId = mapId, PosX = box.PosX, PosY = box.PosY },
            "Opened authored box");
        int openedGold = test.Adventure.Gold;
        test.Relog("opened authored box");
        AssertEqual(openedGold, test.Adventure.Gold, "Opened box relog never re-grants its drop");

        Theatre4GridData shop = Theatre4GridOfType(test, T4GridShop);
        test.ExploreToward(mapId, shop);
        AssertEqual(T4GridShop, test.Grid(mapId, shop.PosX, shop.PosY).Type, "Shop tile keeps its authored content");
        test.Reject(nameof(Theatre4ShopBuyRequest),
            new Theatre4ShopBuyRequest { MapId = mapId, PosX = shop.PosX, PosY = shop.PosY, GoodsIndex = int.MaxValue },
            "Shop good index out of range");
        test.Reject(nameof(Theatre4ShopBuyRequest),
            new Theatre4ShopBuyRequest { MapId = mapId, PosX = shop.PosX, PosY = shop.PosY, GoodsIndex = 0 },
            "Zero shop index");
        test.SetGold(1000);
        int stockBefore = test.Shop(mapId, shop.PosX, shop.PosY).Goods[0].Stock;
        int goldBefore = test.Adventure.Gold;
        test.Call(nameof(Theatre4ShopBuyRequest),
            new Theatre4ShopBuyRequest { MapId = mapId, PosX = shop.PosX, PosY = shop.PosY, GoodsIndex = 1 });
        AssertEqual(stockBefore - 1, test.Shop(mapId, shop.PosX, shop.PosY).Goods[0].Stock,
            "Purchase decrements the offered stock");
        AssertEqual(true, test.Adventure.Gold <= goldBefore, "Purchase never increases gold");
        AssertEqual(1, test.Adventure.EffectShopBuyTimes, "Purchase counter advances once");

        int refreshLimit = TableReaderV2.Parse<Theatre4ShopTable>()
            .Single(row => row.Id == test.Shop(mapId, shop.PosX, shop.PosY).ShopId).RefreshLimit;
        for (int refresh = 0; refresh < refreshLimit; refresh++)
        {
            int goldAtRefresh = test.Adventure.Gold;
            test.Call(nameof(Theatre4RefreshGoodsRequest),
                new Theatre4RefreshGoodsRequest { MapId = mapId, PosX = shop.PosX, PosY = shop.PosY });
            AssertEqual(refresh + 1, test.Shop(mapId, shop.PosX, shop.PosY).RefreshTimes,
                "Refresh increments the authored counter");
            AssertEqual(true, test.Adventure.Gold <= goldAtRefresh, "Refresh never increases gold");
        }
        test.Reject(nameof(Theatre4RefreshGoodsRequest),
            new Theatre4RefreshGoodsRequest { MapId = mapId, PosX = shop.PosX, PosY = shop.PosY },
            "Shop refresh beyond the authored limit");
        test.Reject(nameof(Theatre4RefreshGoodsRequest),
            new Theatre4RefreshGoodsRequest { MapId = mapId, PosX = 0, PosY = 0 }, "Refresh on a non-shop tile");

        Theatre4GridData eventTile = Theatre4GridOfType(test, T4GridEvent);
        test.ExploreToward(mapId, eventTile);
        Theatre4GridData resolved = test.Grid(mapId, eventTile.PosX, eventTile.PosY);
        AssertEqual(true, resolved.Event is not null && resolved.State == T4StateExplored,
            "Generated map always exposes an authored event fixture");
        Theatre4EventTable mapEventRow = TableReaderV2.Parse<Theatre4EventTable>()
            .Single(row => row.Id == resolved.Event!.EventId);
        test.Reject(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = resolved.PosX, PosY = resolved.PosY, Option = int.MaxValue
            }, "Nonzero option on an authored event");
        Theatre4EventOptionTable? option = mapEventRow.OptionGroupId is > 0
            ? TableReaderV2.Parse<Theatre4EventOptionTable>()
                .FirstOrDefault(row => row.GroupId == mapEventRow.OptionGroupId && !(row.OptionShowCondition > 0))
            : null;
        test.Call(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest
            {
                MapId = mapId, PosX = resolved.PosX, PosY = resolved.PosY, Option = option?.Id ?? 0
            });
        Theatre4GridData afterEvent = test.Grid(mapId, resolved.PosX, resolved.PosY);
        AssertEqual(true, afterEvent.State == T4StateProcessed
                || afterEvent.Event?.EventId != mapEventRow.Id,
            "Authored map event resolves its chain exactly once");

        test.Reject(nameof(Theatre4DoFateEventRequest),
            new Theatre4DoFateEventRequest { Option = 0, FateEventUniqueId = int.MaxValue }, "Unknown fate event");
    }

    // Native XTheatre4ConfigModel indexes Theatre4FightGroup by its unique Id, while
    // ContentGroup remains the authored selection bucket used to choose a row.
    private static void ValidateTheatre4FightGroupIdentityCompatibility()
    {
        using Theatre4Case test = new("fight-group-identity");
        test.StartRun(1);
        Theatre4ChapterData chapter = test.Adventure.Chapters[0];
        List<Theatre4FightGroupTable> groups = TableReaderV2.Parse<Theatre4FightGroupTable>();
        Theatre4GridData? target = null;
        Theatre4FightGroupTable? authored = null;
        foreach (Theatre4GridData grid in chapter.Grids)
        {
            if (grid.Fight is not { StageId: > 0 } || grid.ContentGroup <= 0 || grid.ContentId <= 0)
                continue;
            Theatre4FightGroupTable? row = groups.FirstOrDefault(candidate =>
                candidate.GroupId == grid.ContentGroup && candidate.FightId == grid.ContentId
                && groups.Count(other => other.GroupId == candidate.GroupId && other.FightId == candidate.FightId) == 1);
            if (row is not null && row.Id != row.GroupId)
            {
                target = grid;
                authored = row;
                break;
            }
        }
        if (target is null || authored is null)
            throw new InvalidDataException("Fight-group identity fixture has no distinct authored row Id.");

        target = test.ExploreToward(chapter.MapId, target);
        AssertEqual(authored.Id, target.Fight!.FightGroupId,
            "Generated FightData exposes the authored unique row Id");
        AssertEqual(authored.GroupId, target.ContentGroup,
            "Generated fight tile retains its authored selection bucket");
        AssertEqual(authored.FightId, target.ContentId,
            "Generated fight tile retains its authored fight Id");
        AssertEqual(10000, target.Fight.HpPercent,
            "Generated healthy FightData uses native 10000-basis-point HP");


        // Simulate a persisted pre-fix map. Repair must atomically scale the old whole-percent HP
        // and replace the bucket-valued identity; an exact retry must not scale it again.
        target.Fight.FightGroupId = authored.GroupId;
        target.Fight.HpPercent = 50;
        int stateBeforeRepair = target.State;
        test.SaveFixture();
        test.Relog("fight-group-id-repair");
        Theatre4GridData repaired = test.Grid(chapter.MapId, target.PosX, target.PosY);
        AssertEqual(authored.Id, repaired.Fight!.FightGroupId,
            "Relog repairs a legacy bucket-valued FightData identity");
        AssertEqual(5000, repaired.Fight.HpPercent,
            "Legacy whole-percent HP migrates to native basis points exactly once");
        AssertEqual(authored.GroupId, repaired.ContentGroup,
            "Legacy repair preserves the fight selection bucket");
        AssertEqual(authored.FightId, repaired.ContentId,
            "Legacy repair preserves the fight Id");
        AssertEqual(stateBeforeRepair, repaired.State,
            "Legacy repair preserves the tile presentation state");

        test.Relog("fight-group-id-repair-idempotent");
        Theatre4GridData idempotent = test.Grid(chapter.MapId, target.PosX, target.PosY);
        AssertEqual(authored.Id, idempotent.Fight!.FightGroupId,
            "Repeated login keeps the repaired FightData identity");
        AssertEqual(5000, idempotent.Fight.HpPercent,
            "Repeated login does not scale migrated HP a second time");

        // Canonical new data at 1% is already basis points and must never be mistaken for legacy.
        idempotent.Fight.HpPercent = 100;
        test.SaveFixture();
        test.Relog("fight-group-id-canonical-low-hp");
        Theatre4GridData canonical = test.Grid(chapter.MapId, target.PosX, target.PosY);
        AssertEqual(authored.Id, canonical.Fight!.FightGroupId,
            "Canonical low-HP FightData keeps its authored row identity");
        AssertEqual(100, canonical.Fight.HpPercent,
            "Canonical 1% FightData is not multiplied during recovery");

        test.RecruitAndSetTeam();
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = 1, MapId = chapter.MapId, PosX = repaired.PosX, PosY = repaired.PosY
        });
        Theatre4ActiveEncounter located = test.State.ActiveEncounter
            ?? throw new InvalidDataException("Fight-group identity fixture did not freeze an encounter.");
        AssertEqual(1, located.HpPercent,
            "Locate converts native 1% grid HP to the existing whole-percent frozen field");
        AssertEqual(authored.GroupId, located.FightGroupId,
            "Frozen server encounter retains the internal selection bucket");
        byte[] frozen = located.ToBson();
        test.Relog("fight-group-id-active", pending: true);
        AssertEqual(true, frozen.SequenceEqual(test.State.ActiveEncounter!.ToBson()),
            "Relog preserves the active encounter while repairing map identity");
        AssertEqual(authored.GroupId, test.State.ActiveEncounter.FightGroupId,
            "Relog preserves the active encounter's internal bucket");
    }

    // EN XTheatre4BaseGrid:GetGridExploreStep opens a shop only after the explored response
    // carries State=Processed and Shop. Keep the fixture authored instead of trusting a random
    // generated shop/group to contain an unconditional shelf.
    private static void ValidateTheatre4ShopDiscoveryCompatibility()
    {
        using Theatre4Case test = new("shop-discovery");
        test.StartRun(1);
        int mapId = test.Adventure.Chapters[0].MapId;
        List<Theatre4ShopTable> shops = TableReaderV2.Parse<Theatre4ShopTable>();
        List<Theatre4ShopGoodsTable> allGoods = TableReaderV2.Parse<Theatre4ShopGoodsTable>();
        Theatre4ShopGroupTable authoredGroup = TableReaderV2.Parse<Theatre4ShopGroupTable>()
            .First(group =>
            {
                Theatre4ShopTable shop = shops.Single(row => row.Id == group.ShopId);
                return shop.GoodsGroupId.Any(goodsGroup => allGoods.Any(goods =>
                    goods.GroupId == goodsGroup && goods.GoodsType == T4AssetItem
                    && goods.GoodsId is > 0 && goods.Id != goods.GoodsId
                    && !goods.ConditionId.Where(id => id > 0).Any()));
            });
        Theatre4ShopTable authoredShop = shops.Single(row => row.Id == authoredGroup.ShopId);
        List<int> authoredGoods = allGoods
            .Where(goods => authoredShop.GoodsGroupId.Contains(goods.GroupId)
                && !goods.ConditionId.Where(id => id > 0).Any())
            .Select(goods => goods.Id).Take(6).ToList();
        AssertEqual(true, authoredGoods.Count > 0, "Shop fixture has an authored shelf");

        Theatre4GridData host = test.Chapter(mapId).Grids
            .Where(grid => grid.State == T4StateUnknown
                && grid.Type is not (T4GridBoss or T4GridStart or T4GridBuilding
                    or T4GridNothing or T4GridBlank))
            .OrderByDescending(grid => grid.PosX + grid.PosY).FirstOrDefault()
            ?? throw new InvalidDataException("Shop fixture has no unknown traversable tile.");
        int shopX = host.PosX, shopY = host.PosY;
        Theatre4GridData fixture = test.Grid(mapId, shopX, shopY);
        fixture.Type = T4GridShop;
        fixture.ContentGroup = authoredGroup.ShopGroupId;
        fixture.ContentId = authoredShop.Id;
        fixture.Fight = null;
        fixture.Event = null;
        fixture.Building = null;
        fixture.Shop = null;
        fixture.State = T4StateUnknown;
        Theatre4EffectTable randomSale = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 26);
        Theatre4EffectTable firstPurchaseFree = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 202);
        Theatre4SeedEffect(test, randomSale.Id);
        Theatre4SeedEffect(test, firstPurchaseFree.Id);

        test.ExploreUntilDiscover(mapId, fixture);
        Theatre4GridData undiscovered = test.Grid(mapId, shopX, shopY);
        AssertEqual(null, undiscovered.Shop, "Hidden shop does not pre-open its shelf");
        test.Adventure.Ap = Math.Max(1, test.Adventure.Ap);
        test.SaveFixture();
        JObject explored = test.Call(nameof(Theatre4ExploreGridRequest),
            new Theatre4ExploreGridRequest { MapId = mapId, PosX = shopX, PosY = shopY });
        JObject responseGrid = RequiredObject(explored, "Grid", "shop discovery grid");
        AssertEqual(T4StateProcessed, RequiredValue<int>(responseGrid, "State", JTokenType.Integer,
            "shop discovery state"), "Discovering a shop completes its tile for the client");
        JObject responseShop = RequiredObject(responseGrid, "Shop", "shop discovery payload");
        AssertEqual(authoredShop.Id, RequiredValue<int>(responseShop, "ShopId", JTokenType.Integer,
            "shop discovery id"), "Shop discovery returns the authored shop identity");
        JArray responseGoods = RequiredValue<JArray>(responseShop, "Goods", JTokenType.Array,
            "shop discovery goods");
        AssertEqual(string.Join(",", authoredGoods),
            string.Join(",", responseGoods.Select(goods => RequiredValue<int>((JObject)goods, "GoodsId",
                JTokenType.Integer, "shop discovery good"))),
            "Shop discovery returns eligible authored goods in order, capped at six");
        Theatre4ShopData opened = test.Shop(mapId, shopX, shopY);
        AssertEqual(true, opened.Goods.Count == authoredGoods.Count,
            "Opened shop persists its shelf before purchase");

        AssertEqual(1, opened.Goods.Count(goods => goods.IsFree),
            "Random Sale marks exactly one newly opened shelf column free");
        AssertEqual(true, opened.FreeBuyTimes > 0,
            "First Purchase Free opens its authored one-purchase allowance");
        int purchaseGoodsIndex = authoredGoods.FindIndex(goodsId =>
            allGoods.Single(row => row.Id == goodsId).GoodsType == T4AssetItem) + 1;
        if (purchaseGoodsIndex <= 0)
            throw new InvalidDataException("Authored shop shelf has no purchasable item row.");
        Theatre4ShopGoodsTable firstGoods = allGoods.Single(row => row.Id == authoredGoods[purchaseGoodsIndex - 1]);
        test.SetGold(1_000_000);
        int purchasedItemId = firstGoods.GoodsId
            ?? throw new InvalidDataException("Authored item shop row has no item id.");
        long purchasedBefore = Theatre4ProgressModeUnits(test.Adventure, firstGoods.GoodsType, purchasedItemId);
        long configIdBefore = Theatre4ProgressModeUnits(test.Adventure, firstGoods.GoodsType, firstGoods.Id);
        int goldBefore = test.Adventure.Gold;
        Theatre4ShopGoodsData purchasedGoods = opened.Goods[purchaseGoodsIndex - 1];
        int expectedPrice = opened.FreeBuyTimes > 0 ? 0
            : purchasedGoods.IsFree ? 0
            : (int)((long)firstGoods.Price * (10000 + Math.Max(-10000, opened.Discount)) / 10000);
        JObject bought = test.Call(nameof(Theatre4ShopBuyRequest),
            new Theatre4ShopBuyRequest { MapId = mapId, PosX = shopX, PosY = shopY, GoodsIndex = purchaseGoodsIndex });
        JObject boughtShop = RequiredObject(bought, "Shop", "shop purchase response");
        AssertEqual(0, RequiredValue<int>(RequiredValue<JArray>(boughtShop, "Goods", JTokenType.Array,
            "shop purchase goods")[purchaseGoodsIndex - 1] as JObject
                ?? throw new InvalidDataException("Missing shop purchase good"),
            "Stock", JTokenType.Integer, "shop purchase stock"), "Purchase exhausts the authored stock");
        AssertEqual(goldBefore - expectedPrice, test.Adventure.Gold,
            "Purchase applies the authored price and discount");
        AssertEqual(purchasedBefore + Math.Max(1, firstGoods.GoodsNum),
            Theatre4ProgressModeUnits(test.Adventure, firstGoods.GoodsType, purchasedItemId),
            "Purchase grants the authored GoodsId mode asset, not the shop row Id");
        if (firstGoods.Id != purchasedItemId)
            AssertEqual(configIdBefore,
                Theatre4ProgressModeUnits(test.Adventure, firstGoods.GoodsType, firstGoods.Id),
                "Purchase does not grant the authored shop configuration Id");
        Theatre4ShopData afterPurchase = test.Shop(mapId, shopX, shopY);
        AssertEqual(0, afterPurchase.Goods.Count(goods => goods.IsFree),
            "Any shop purchase clears every Random Sale free column");
        AssertEqual(1, test.Adventure.EffectShopBuyTimes, "Purchase increments the observable shop counter");
        test.Reject(nameof(Theatre4ShopBuyRequest),
            new Theatre4ShopBuyRequest { MapId = mapId, PosX = shopX, PosY = shopY, GoodsIndex = purchaseGoodsIndex },
            "Sold-out shop purchase");
        test.Relog("shop discovery");
        AssertEqual(0, test.Shop(mapId, shopX, shopY).Goods[purchaseGoodsIndex - 1].Stock,
            "Opened shop stock survives relog");
        test.Call(nameof(Theatre4RefreshGoodsRequest),
            new Theatre4RefreshGoodsRequest { MapId = mapId, PosX = shopX, PosY = shopY });
        AssertEqual(0, test.Shop(mapId, shopX, shopY).Goods.Count(goods => goods.IsFree),
            "Random Sale marker prevents a free column after refresh");
    }

    private static void ValidateTheatre4BoxRecoveryCompatibility()
    {
        List<Theatre4BoxGroupTable> rows = TableReaderV2.Parse<Theatre4BoxGroupTable>();
        Theatre4BoxGroupTable authored = rows.First(row => row.Id != row.DropId);
        int recoveredId = rows.Where(row => row.GroupId == authored.GroupId && row.DropId == authored.DropId)
            .Min(row => row.Id);

        using Theatre4Case test = new("legacy-box-recovery");
        test.StartRun(1);
        Theatre4GridData live = Theatre4GridOfType(test, T4GridBox);
        live.ContentGroup = authored.GroupId;
        live.ContentId = authored.DropId;
        test.State.TracebackSnapshots[1] = MessagePackSerializer.Deserialize<Theatre4AdventureData>(
            MessagePackSerializer.Serialize(test.Adventure));
        test.State.PendingSettleAdventure = MessagePackSerializer.Deserialize<Theatre4AdventureData>(
            MessagePackSerializer.Serialize(test.Adventure));
        test.SaveFixture();
        test.Relog("legacy drop-id boxes");

        Theatre4AdventureData[] recovered =
        [
            test.Adventure,
            test.State.TracebackSnapshots[1],
            test.State.PendingSettleAdventure!
        ];
        foreach (Theatre4AdventureData snapshot in recovered)
        {
            Theatre4GridData box = snapshot.Chapters.SelectMany(chapter => chapter.Grids)
                .Single(grid => grid.GridId == live.GridId);
            AssertEqual(authored.GroupId, box.ContentGroup, "Legacy box recovery preserves its authored group");
            AssertEqual(recoveredId, box.ContentId, "Legacy box recovery chooses the lowest matching authored row");
            Theatre4BoxGroupTable row = rows.Single(candidate => candidate.Id == box.ContentId);
            AssertEqual(authored.DropId, row.DropId, "Recovered box preserves its exact authored drop");
            AssertEqual(true, !string.IsNullOrWhiteSpace(row.Icon) && row.BlockIcon > 0,
                "Recovered box identity has authored render assets");
        }

        using Theatre4Case rollback = new("legacy-box-recovery-rollback");
        rollback.StartRun(1);
        Theatre4GridData rollbackBox = Theatre4GridOfType(rollback, T4GridBox);
        rollbackBox.ContentGroup = authored.GroupId;
        rollbackBox.ContentId = authored.DropId;
        rollback.SaveFixture();
        byte[] before = MessagePackSerializer.Serialize(rollback.Adventure);
        rollback.Players.ThrowOnReplaceOne = true;
        try
        {
            bool failed = false;
            try
            {
                RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre4Module"), "PrepareLogin",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Session)])
                    .Invoke(null, [rollback.Session]);
            }
            catch (TargetInvocationException)
            {
                failed = true;
            }
            AssertEqual(true, failed, "Legacy box recovery surfaces persistence failure");
        }
        finally
        {
            rollback.Players.ThrowOnReplaceOne = false;
        }
        AssertEqual(true, before.SequenceEqual(MessagePackSerializer.Serialize(rollback.Adventure)),
            "Failed legacy box recovery leaves the active snapshot unchanged until relog");
    }

    //endregion

    //region economy (recruit, drops, recycling, talents)

    private static void ValidateTheatre4EconomyCompatibility()
    {
        using Theatre4Case test = new("economy");
        test.StartRun(1);

        Theatre4TransactionData recruit = test.Adventure.Transactions.FirstOrDefault(transaction => transaction.Type == 1)
            ?? throw new InvalidDataException("Affix recruit did not create a recruit transaction.");
        int recruitId = recruit.Id;
        AssertEqual(true, recruit.Characters.Count > 0, "Recruit offer rolls authored characters");
        test.Reject(nameof(Theatre4ConfirmRecruitRequest),
            new Theatre4ConfirmRecruitRequest { TransactionId = int.MaxValue, Index = 1 }, "Unknown recruit transaction");
        test.Reject(nameof(Theatre4ConfirmRecruitRequest),
            new Theatre4ConfirmRecruitRequest { TransactionId = recruitId, Index = int.MaxValue },
            "Recruit index off the offered roster");
        test.Call(nameof(Theatre4RefreshRecruitRequest), new Theatre4RefreshRecruitRequest { TransactionId = recruitId });
        Theatre4TransactionData refreshed = test.Adventure.Transactions
            .First(transaction => transaction.Id == recruitId);
        AssertEqual(refreshed.RefreshLimit - 1, refreshed.RefreshTimes, "Recruit refresh spends exactly one ticket refresh");
        int offeredCharacter = refreshed.Characters[^1].CharacterId;
        test.Call(nameof(Theatre4ConfirmRecruitRequest),
            new Theatre4ConfirmRecruitRequest { TransactionId = recruitId, Index = refreshed.Characters.Count });
        AssertEqual(true, test.Adventure.Characters.Any(character => character.CharacterId == offeredCharacter),
            "Confirmed recruit joins the run roster");
        test.Reject(nameof(Theatre4RefreshRecruitRequest), new Theatre4RefreshRecruitRequest { TransactionId = recruitId },
            "Recruit refresh after the ticket is spent");

        // A Type-2 drop rolls its authored groups into a single-pick offer. Require a
        // deterministic authored fixture; silently replacing it with an unknown-transaction
        // rejection would leave the success path untested.
        Theatre4RewardDropTable pickDrop = TableReaderV2.Parse<Theatre4RewardDropTable>()
            .First(row => row.Type == 2 && row.GroupIds.Count > 0
                && row.Probabilitys.Count >= row.GroupIds.Count
                && Enumerable.Range(0, row.GroupIds.Count).All(index => row.Probabilitys[index] >= 1));
        Theatre4GrantDrop(test, pickDrop.Id);
        Theatre4TransactionData pending = test.Adventure.Transactions.First(transaction => transaction.Type == 3);
        test.Reject(nameof(Theatre4ConfirmDropRequest),
            new Theatre4ConfirmDropRequest { TransactionId = pending.Id, Index = int.MaxValue }, "Drop index off offer");
        test.Call(nameof(Theatre4ConfirmDropRequest),
            new Theatre4ConfirmDropRequest { TransactionId = pending.Id, Index = 1 });
        AssertEqual(true, test.Adventure.Transactions.All(transaction => transaction.Id != pending.Id),
            "Single-pick drop retires after confirmation");

        HashSet<int> deterministicDrops = Theatre4DeterministicDrops();
        List<Theatre4RewardDropTable> rewardDrops = TableReaderV2.Parse<Theatre4RewardDropTable>();
        List<Theatre4RewardTable> rewardRows = TableReaderV2.Parse<Theatre4RewardTable>();
        Theatre4FightTable fight = TableReaderV2.Parse<Theatre4FightTable>()
            .First(row => row.RewardDropId is > 0 && deterministicDrops.Contains(row.RewardDropId.Value)
                && rewardDrops.Single(drop => drop.Id == row.RewardDropId.Value).GroupIds
                    .Where(group => group > 0)
                    .All(group => rewardRows.Any(reward => reward.GroupId == group
                        && reward.ElementType > 0 && reward.Condition is not > 0)));
        Theatre4TransactionData reward = Theatre4CreateFightReward(test, fight.Id)
            ?? throw new InvalidDataException($"Authored fight {fight.Id} did not create a reward offer.");
        int rewardId = reward.Id;
        test.Reject(nameof(Theatre4ConfirmFightRewardRequest),
            new Theatre4ConfirmFightRewardRequest { TransactionId = rewardId, Index = int.MaxValue },
            "Fight reward index off offer");
        test.Call(nameof(Theatre4ConfirmFightRewardRequest),
            new Theatre4ConfirmFightRewardRequest { TransactionId = rewardId, Index = 1 });
        AssertEqual(true, test.Adventure.Transactions.All(transaction => transaction.Id != rewardId)
                || test.Adventure.Transactions.First(transaction => transaction.Id == rewardId).SelectIds.Count == 1,
            "Fight reward records the claimed index once");
        test.Reject(nameof(Theatre4ConfirmFightRewardRequest),
            new Theatre4ConfirmFightRewardRequest { TransactionId = rewardId, Index = 1 },
            "Fight reward cannot claim an index twice");

        Theatre4TransactionData quit = Theatre4CreateFightReward(test, fight.Id)
            ?? throw new InvalidDataException($"Authored fight {fight.Id} did not recreate a reward offer.");
        AssertEqual(true, test.Adventure.Transactions.Any(transaction => transaction.Id == quit.Id),
            "Fight reward recreation remains an active offer");
        test.Call(nameof(Theatre4QuitFightRewardRequest),
            new Theatre4QuitFightRewardRequest { TransactionId = quit.Id });
        AssertEqual(true, test.Adventure.Transactions.All(transaction => transaction.Id != quit.Id),
            "Quit forfeits the whole fight reward offer");
        test.Reject(nameof(Theatre4QuitFightRewardRequest),
            new Theatre4QuitFightRewardRequest { TransactionId = int.MaxValue }, "Quit an unknown fight reward");

        Theatre4ItemTable blueprint = TableReaderV2.Parse<Theatre4ItemTable>()
            .First(row => row.IsProp is not > 0 && (row.BackPrice ?? 0) > 0);
        Theatre4SeedItem(test, blueprint.Id);
        Theatre4ItemData owned = test.Adventure.Items.First(item => item.ItemId == blueprint.Id);
        int ownedUid = owned.Uid;
        int goldBeforeRecycle = test.Adventure.Gold;
        test.Call(nameof(Theatre4ItemRecyclingRequest), new Theatre4ItemRecyclingRequest { Uid = ownedUid });
        AssertEqual(false, test.Adventure.Items.Any(item => item.Uid == ownedUid), "Recycled blueprint leaves the bag");
        AssertEqual(goldBeforeRecycle + (blueprint.BackPrice ?? 0), test.Adventure.Gold,
            "Recycling credits the authored back price once");
        test.Reject(nameof(Theatre4ItemRecyclingRequest), new Theatre4ItemRecyclingRequest { Uid = ownedUid },
            "Recycling the same blueprint twice");
        test.Reject(nameof(Theatre4WaitItemRecyclingRequest),
            new Theatre4WaitItemRecyclingRequest { ItemId = blueprint.Id }, "Recycling an un-waiting blueprint");
        test.Reject(nameof(Theatre4ReplaceItemRequest),
            new Theatre4ReplaceItemRequest { WaitItemId = blueprint.Id, TargetItemUid = ownedUid },
            "Replacing an un-waiting blueprint");
        test.Reject(nameof(Theatre4ConfirmItemRequest),
            new Theatre4ConfirmItemRequest { TransactionId = int.MaxValue, OperateType = 1, Index = 1 },
            "Unknown item transaction");

        // Box -> Item transaction success path: the authored box rolls one blueprint option,
        // awarding installs it, recycling credits its back price, and the box retires.
        Theatre4ItemBoxTable box = TableReaderV2.Parse<Theatre4ItemBoxTable>()
            .First(row => row.ItemGroupId.Any(group => group > 0));
        Theatre4AddAsset(test, 1, box.Id, 1);
        Theatre4TransactionData itemOffer = test.Adventure.Transactions.First(transaction => transaction.Type == 2);
        int itemOfferId = itemOffer.Id;
        AssertEqual(true, itemOffer.Rewards.Count > 0, "Opened authored box offers a blueprint");
        test.Reject(nameof(Theatre4ConfirmItemRequest),
            new Theatre4ConfirmItemRequest { TransactionId = itemOfferId, OperateType = 3, Index = 1 },
            "Unsupported item operate type");
        test.Reject(nameof(Theatre4ConfirmItemRequest),
            new Theatre4ConfirmItemRequest { TransactionId = itemOfferId, OperateType = 1, Index = int.MaxValue },
            "Item index off the offered box");
        int offeredItem = itemOffer.Rewards[0].Id;
        test.Call(nameof(Theatre4ConfirmItemRequest),
            new Theatre4ConfirmItemRequest { TransactionId = itemOfferId, OperateType = 1, Index = 1 });
        AssertEqual(true, test.Adventure.Items.Any(item => item.ItemId == offeredItem),
            "Award delivers the offered blueprint");
        AssertEqual(false, test.Adventure.ItemBoxs.Contains(box.Id), "Consumed box leaves the owned ledger");
        test.Reject(nameof(Theatre4ConfirmItemRequest),
            new Theatre4ConfirmItemRequest { TransactionId = itemOfferId, OperateType = 1, Index = 1 },
            "Item offer cannot be claimed twice");

        Theatre4AddAsset(test, 1, box.Id, 1);
        Theatre4TransactionData recycleOffer = test.Adventure.Transactions.First(transaction => transaction.Type == 2);
        int recycleGold = test.Adventure.Gold;
        test.Call(nameof(Theatre4ConfirmItemRequest),
            new Theatre4ConfirmItemRequest { TransactionId = recycleOffer.Id, OperateType = 2, Index = 1 });
        AssertEqual(true, test.Adventure.Gold >= recycleGold, "Recycling an offered blueprint is credited, never debited");

        // Overflow -> WaitItems: a blueprint beyond the run ItemLimit waits instead of vanishing,
        // and the wait-recycle/replace pair both resolve through the real RPCs.
        test.SetItemLimit(0);
        Theatre4AddAsset(test, T4AssetItem, blueprint.Id, 1);
        AssertEqual(true, test.Adventure.WaitItems.Contains(blueprint.Id),
            "Blueprint overflow waits instead of vanishing");
        int waitGold = test.Adventure.Gold;
        test.Call(nameof(Theatre4WaitItemRecyclingRequest),
            new Theatre4WaitItemRecyclingRequest { ItemId = blueprint.Id });
        AssertEqual(false, test.Adventure.WaitItems.Contains(blueprint.Id), "Waiting blueprint recycles away");
        AssertEqual(waitGold + (blueprint.BackPrice ?? 0), test.Adventure.Gold,
            "Waiting blueprint credits its authored back price");

        Theatre4AddAsset(test, T4AssetItem, blueprint.Id, 1);
        test.Call(nameof(Theatre4WaitItemRecyclingRequest),
            new Theatre4WaitItemRecyclingRequest { ItemId = blueprint.Id });
        test.SetItemLimit(8);
        Theatre4AddAsset(test, T4AssetItem, blueprint.Id, 1);
        int replaceTargetUid = test.Adventure.Items.First(item => item.ItemId == blueprint.Id).Uid;
        test.SetItemLimit(0);
        Theatre4AddAsset(test, T4AssetItem, blueprint.Id, 1);
        AssertEqual(true, test.Adventure.WaitItems.Contains(blueprint.Id), "Replacement overflow waits");
        test.SetItemLimit(8);
        test.Call(nameof(Theatre4ReplaceItemRequest),
            new Theatre4ReplaceItemRequest { WaitItemId = blueprint.Id, TargetItemUid = replaceTargetUid });
        AssertEqual(false, test.Adventure.WaitItems.Contains(blueprint.Id), "Replace consumes the waiting blueprint");
        AssertEqual(false, test.Adventure.Items.Any(item => item.Uid == replaceTargetUid),
            "Replace consumes the owned target");
        AssertEqual(true, test.Adventure.Items.Any(item => item.ItemId == blueprint.Id),
            "Replace installs the waited blueprint");

        // Colour talent: account-paced points open an authored slot offer; refresh honours the
        // authored limit and selection installs exactly the offered talent.
        Theatre4ColorTalentSlotTable slot = TableReaderV2.Parse<Theatre4ColorTalentSlotTable>()
            .Where(row => row.Level > 0 && row.UnlockPoint is > 0)
            .OrderBy(row => row.UnlockPoint).First();
        Theatre4AddAsset(test, T4AssetColorPoint, slot.Color, slot.UnlockPoint ?? 0);
        AssertEqual(true, test.Adventure.Colors.Single(candidate => candidate.Color == slot.Color).WaitSlot is not null,
            "Reaching the authored unlock point opens a talent offer");
        test.Reject(nameof(Theatre4SelectTalentRequest),
            new Theatre4SelectTalentRequest { Color = slot.Color, TalentId = int.MaxValue }, "Unoffered talent");
        int refreshLimit = TableReaderV2.Parse<Theatre4ActivityTable>().Single(row => row.Id == test.Data.ActivityId)
            .TalentRefreshLimitNum;
        // The refresh ladder is paid after any effect-granted free refreshes.
        test.SetGold(1000);
        for (int refresh = 0; refresh < refreshLimit; refresh++)
            test.Call(nameof(Theatre4RefreshTalentRequest), new Theatre4RefreshTalentRequest { Color = slot.Color });
        test.Reject(nameof(Theatre4RefreshTalentRequest), new Theatre4RefreshTalentRequest { Color = slot.Color },
            "Talent refresh beyond the authored limit");
        int offered = test.Adventure.Colors.Single(candidate => candidate.Color == slot.Color).WaitSlot!.TalentIds[0];
        test.Call(nameof(Theatre4SelectTalentRequest),
            new Theatre4SelectTalentRequest { Color = slot.Color, TalentId = offered });
        AssertEqual(true, test.Adventure.Colors.Single(candidate => candidate.Color == slot.Color)
            .Slots.SelectMany(existing => existing.Talents).Any(talent => talent.TalentId == offered),
            "Selected talent is installed in the authored slot");
        AssertEqual(true, test.Data.TalentAtlas.Contains(offered), "Selected talent enters the permanent atlas");
    }

    //endregion

    //region effects (skills, timeback)

    private static void ValidateTheatre4EffectCompatibility()
    {
        using Theatre4Case test = new("effects");
        test.StartRun(1);
        int mapId = test.Adventure.Chapters[0].MapId;

        test.Reject(nameof(Theatre4UseSkillEffectRequest),
            new Theatre4UseSkillEffectRequest { EffectId = int.MaxValue }, "Unknown skill effect");
        Theatre4EffectTable build = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 101 && row.Params.Count > 0
                && TableReaderV2.Parse<Theatre4BuildingTable>().Any(building => building.Id == row.Params[0]));
        test.Reject(nameof(Theatre4UseSkillEffectRequest),
            new Theatre4UseSkillEffectRequest { EffectId = build.Id }, "Skill effect not owned");

        Theatre4GridData empty = Theatre4GridOfType(test, T4GridEmpty);
        test.ExploreToward(mapId, empty);
        Theatre4GridData emptyGrid = test.Grid(mapId, empty.PosX, empty.PosY);
        AssertEqual(T4StateProcessed, emptyGrid.State, "Empty tile completes on exploration");
        int targetX = emptyGrid.PosX, targetY = emptyGrid.PosY;

        Theatre4SeedEffect(test, build.Id);
        test.SetBuildPoint(0);
        byte[] beforeRejection = test.State.ToBson();
        test.Call(nameof(Theatre4UseSkillEffectRequest),
            new Theatre4UseSkillEffectRequest { EffectId = build.Id, Params = [mapId, targetY, targetX] },
            success: false);
        AssertEqual(true, beforeRejection.SequenceEqual(test.State.ToBson()),
            "Unaffordable build skill leaves the adventure and target untouched");

        int buildCost = build.SkillCostType == T4AssetBuildPoint ? Math.Max(0, build.SkillCostCount ?? 0) : 0;
        test.SetBuildPoint(Math.Max(1, buildCost + 1));
        int bpBefore = test.Asset(T4AssetBuildPoint, 0);
        test.Call(nameof(Theatre4UseSkillEffectRequest),
            new Theatre4UseSkillEffectRequest { EffectId = build.Id, Params = [mapId, targetY, targetX] });
        AssertEqual(T4GridBuilding, test.Grid(mapId, targetX, targetY).Type, "Build skill installs the authored building");
        if (build.SkillCostType == T4AssetBuildPoint)
            AssertEqual(bpBefore - buildCost, test.Asset(T4AssetBuildPoint, 0), "Build skill charges the authored cost");
        Theatre4ChapterData chapter = test.Chapter(mapId);
        AscNet.Table.V2.share.theatre4.theatre4map.Theatre4MapTable map =
            TableReaderV2.Parse<AscNet.Table.V2.share.theatre4.theatre4map.Theatre4MapTable>()
                .Single(row => row.Id == mapId);
        List<(int X, int Y)> outsideFloor = Enumerable.Range(0, map.SizeX)
            .SelectMany(x => Enumerable.Range(0, map.SizeY).Select(y => (X: x, Y: y)))
            .Where(position => chapter.Grids.All(grid => grid.PosX != position.X || grid.PosY != position.Y))
            .Where(position => T4EffectAuditShapePositionAvailable(chapter, map, position.X, position.Y))
            .ToList();
        if (outsideFloor.Count == 0)
            throw new InvalidDataException("Construction fixture has no in-bounds tile outside closed regions.");
        (int nothingX, int nothingY) = outsideFloor[0];
        Theatre4GridData unknownOutsideFloor = T4EffectAuditAuthoredGrid(chapter, map, nothingX, nothingY);
        unknownOutsideFloor.Type = T4GridNothing;
        unknownOutsideFloor.State = T4StateUnknown;
        test.SaveFixture();
        test.Reject(nameof(Theatre4UseSkillEffectRequest),
            new Theatre4UseSkillEffectRequest
            {
                EffectId = build.Id,
                Params = [mapId, unknownOutsideFloor.PosY, unknownOutsideFloor.PosX]
            }, "Building on an unknown outside-floor tile");

        test.Reject(nameof(Theatre4UseSkillEffectRequest),
            new Theatre4UseSkillEffectRequest { EffectId = build.Id, Params = [mapId, targetY, targetX] },
            "Build skill on a non-empty target");

        // A passed chapter can never be mutated by an active skill (shared Explore gate).
        test.SetBuildPoint(500);
        test.Adventure.Chapters[0].IsPass = true;
        test.SaveFixture();
        test.Reject(nameof(Theatre4UseSkillEffectRequest),
            new Theatre4UseSkillEffectRequest { EffectId = build.Id, Params = [mapId, targetY, targetX] },
            "Skill on a passed chapter");
        test.Adventure.Chapters[0].IsPass = false;
        test.SaveFixture();

        // Type412 "set asset to a value": applies on the next real rebuild, lowers a positive
        // balance exactly once, and is never reapplied by a relog.
        Theatre4EffectTable setter = TableReaderV2.Parse<Theatre4EffectTable>().First(row => row.Type == 412
            && row.Params.Count >= 3 && row.Params[0] == T4AssetBuildPoint && row.Params[2] == 0);
        Theatre4SeedEffect(test, setter.Id);
        Theatre4GridData secondEmpty = Theatre4EmptyTile(test, mapId);
        test.SetBuildPoint(500);
        test.Call(nameof(Theatre4UseSkillEffectRequest),
            new Theatre4UseSkillEffectRequest { EffectId = build.Id, Params = [mapId, secondEmpty.PosY, secondEmpty.PosX] });
        AssertEqual(0, test.Asset(T4AssetBuildPoint, 0), "Type412 lowers the positive balance to its authored target");
        test.SetBuildPoint(500);
        test.Relog("set asset effect");
        AssertEqual(500, test.Asset(T4AssetBuildPoint, 0),
            "Type412 does not reset newly earned balance on relog");

        Theatre4EffectTable alter = TableReaderV2.Parse<Theatre4EffectTable>().First(row => row.Type == 115);
        test.SetBuildPoint(Math.Max(1, alter.SkillCostType == T4AssetBuildPoint ? alter.SkillCostCount ?? 0 : 0));

        Theatre4SeedEffect(test, alter.Id);
        Theatre4GridData recolor = test.Grid(mapId, nothingX, nothingY);
        recolor.Type = T4GridEmpty;
        recolor.State = T4StateDiscover;
        recolor.Color = test.Adventure.Colors.First(color => color.Color != alter.Params[0]).Color;
        test.SaveFixture();
        int recolorX = recolor.PosX, recolorY = recolor.PosY;
        test.Call(nameof(Theatre4UseSkillEffectRequest),
            new Theatre4UseSkillEffectRequest { EffectId = alter.Id, Params = [mapId, recolorY, recolorX] });
        AssertEqual(alter.Params[0], test.Grid(mapId, recolorX, recolorY).Color,
            "Alter skill applies the authored colour to the active chapter");

        Theatre4EffectTable timeback = TableReaderV2.Parse<Theatre4EffectTable>()
            .First(row => row.Type == 423);
        Theatre4SeedEffect(test, timeback.Id);
        while (test.Adventure.Days < 3)
        {
            test.Adventure.Ap = 0;
            test.SaveFixture();
            test.Call(nameof(Theatre4DailySettleRequest));
        }
        Theatre4AddAsset(test, T4AssetTimeBack, 0, 1);
        int daysBefore = test.Adventure.Days;
        // Real client sends no params; the server selects the authored rollback day.
        test.Call(nameof(Theatre4UseSkillEffectRequest), new Theatre4UseSkillEffectRequest { EffectId = timeback.Id });
        AssertEqual(0, test.Asset(T4AssetTimeBack, 0), "Timeback charges exactly one authored point");
        AssertEqual(true, test.Adventure.Days < daysBefore, "Timeback rolls the sand table back to the authored day");
    }

    // EN XTheatre4CharacterData stores the star-preview ColorLevelAdds separately from
    // persistent effects. Exercise both the first recruit and a duplicate star-up through
    // the registered handler and compare each visible payload with the authored star table.
    private static void ValidateTheatre4CharacterStarCompatibility()
    {
        Theatre4CharacterTable character = TableReaderV2.Parse<Theatre4CharacterTable>()
            .Where(row => row.Id > 0 && row.StarGroupId > 0)
            .First(row =>
            {
                List<Theatre4CharacterStarTable> stars = TableReaderV2.Parse<Theatre4CharacterStarTable>()
                    .Where(star => star.GroupId == row.StarGroupId && star.Star is 1 or 2).ToList();
                return stars.Any(star => star.Star == 1 && star.ColorLevel.Any(value => value > 0))
                    && stars.Any(star => star.Star == 2 && star.ColorLevel.Any(value => value > 0));
            });
        List<Theatre4CharacterStarTable> authoredStars = TableReaderV2.Parse<Theatre4CharacterStarTable>()
            .Where(star => star.GroupId == character.StarGroupId && star.Star is 1 or 2).ToList();
        Theatre4CharacterStarTable starOne = authoredStars.Single(star => star.Star == 1);
        Theatre4CharacterStarTable starTwo = authoredStars.Single(star => star.Star == 2);
        static Dictionary<int, int> ColorAdds(Theatre4CharacterStarTable star) =>
            star.ColorLevel.Select((value, index) => (Color: index + 1, Value: value))
                .Where(pair => pair.Value != 0).ToDictionary(pair => pair.Color, pair => pair.Value);
        Dictionary<int, int> expectedOne = ColorAdds(starOne);
        Dictionary<int, int> expectedTwo = ColorAdds(starTwo);

        using Theatre4Case test = new("character-star");
        test.StartRun(1);
        test.Adventure.Transactions.RemoveAll(transaction => transaction.Type == 1);
        Theatre4TransactionData firstOffer = new()
        {
            Id = ++test.Adventure.IdSequence,
            Type = 1,
            Characters = [new Theatre4CharacterData { CharacterId = character.Id, Star = 1 }],
            SelectLimit = 1
        };
        test.Adventure.Transactions.Add(firstOffer);
        test.SaveFixture();
        HashSet<int> effectsBefore = test.Adventure.CustomEffects.Select(effect => effect.EffectId).ToHashSet();
        JObject firstResponse = test.Call(nameof(Theatre4ConfirmRecruitRequest),
            new Theatre4ConfirmRecruitRequest { TransactionId = firstOffer.Id, Index = 1 });
        Theatre4CharacterData first = test.Adventure.Characters.Single(candidate => candidate.CharacterId == character.Id);
        AssertEqual(true, expectedOne.OrderBy(pair => pair.Key).SequenceEqual(first.ColorLevelAdds.OrderBy(pair => pair.Key)),
            "First recruit stores authored star ColorLevelAdds");
        AssertEqual(true, effectsBefore.SetEquals(test.Adventure.CustomEffects.Select(effect => effect.EffectId)),
            "Recruit star preview never invents a custom effect");
        NotifyTheatre4CharacterUpdate firstPush = MessagePackSerializer.Deserialize<NotifyTheatre4CharacterUpdate>(
            test.Pushes.Single(push => push.Name == nameof(NotifyTheatre4CharacterUpdate)).Content);
        AssertEqual(true, firstPush.Character is not null
            && expectedOne.OrderBy(pair => pair.Key).SequenceEqual(firstPush.Character.ColorLevelAdds.OrderBy(pair => pair.Key)),
            "Recruit response push exposes the authored star preview");
        AssertEqual(character.Id, first.CharacterId, "Recruit response uses the authored character row identity");
        AssertEqual(true, firstResponse.Value<int>("Code") == 0, "Recruit response succeeds");

        Theatre4TransactionData secondOffer = new()
        {
            Id = ++test.Adventure.IdSequence,
            Type = 1,
            Characters = [new Theatre4CharacterData { CharacterId = character.Id, Star = 2 }],
            SelectLimit = 1
        };
        test.Adventure.Transactions.Add(secondOffer);
        test.SaveFixture();
        test.Call(nameof(Theatre4ConfirmRecruitRequest),
            new Theatre4ConfirmRecruitRequest { TransactionId = secondOffer.Id, Index = 1 });
        Theatre4CharacterData upgraded = test.Adventure.Characters.Single(candidate => candidate.CharacterId == character.Id);
        AssertEqual(2, upgraded.Star, "Duplicate recruit advances to the authored star");
        AssertEqual(true, expectedTwo.OrderBy(pair => pair.Key).SequenceEqual(upgraded.ColorLevelAdds.OrderBy(pair => pair.Key)),
            "Duplicate recruit replaces the preview with the authored next-star values");
        test.Relog("character-star");
        Theatre4CharacterData relogged = test.Adventure.Characters.Single(candidate => candidate.CharacterId == character.Id);
        AssertEqual(true, expectedTwo.OrderBy(pair => pair.Key).SequenceEqual(relogged.ColorLevelAdds.OrderBy(pair => pair.Key)),
            "Character star preview survives relog");
    }

    //endregion

    //region grid event chain (real DoGridEvent guards)

    private static void ValidateTheatre4GridEventCompatibility()
    {
        using Theatre4Case test = new("grid-event-chain");
        test.StartRun(1);
        int mapId = test.Adventure.Chapters[0].MapId;

        // Authored option-bearing event whose chosen option advances to a authored next event.
        Theatre4EventTable choiceEvent = TableReaderV2.Parse<Theatre4EventTable>()
            .First(row => row.OptionGroupId is > 0 && TableReaderV2.Parse<Theatre4EventOptionTable>()
                .Any(option => option.GroupId == row.OptionGroupId && option.NextEvent is > 0
                    && TableReaderV2.Parse<Theatre4EventTable>().Any(next => next.Id == option.NextEvent)
                    && !(option.OptionCondition > 0) && !(option.OptionShowCondition > 0)));

        Theatre4GridData unexplored = Theatre4EventTile(test, mapId, choiceEvent.Id, T4StateDiscover);
        test.Reject(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = unexplored.PosX, PosY = unexplored.PosY, Option = 0 },
            "Event on an unexplored tile");

        // Option-less authored event: a nonzero Option is never a dialogue.
        Theatre4EventTable dialogue = TableReaderV2.Parse<Theatre4EventTable>()
            .First(row => !(row.OptionGroupId > 0) && row.Type != 4);
        Theatre4GridData dialogueTile = Theatre4EventTile(test, mapId, dialogue.Id, T4StateExplored);
        test.Reject(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = dialogueTile.PosX, PosY = dialogueTile.PosY, Option = int.MaxValue },
            "Nonzero option on an option-less event");

        Theatre4EventOptionTable progression = TableReaderV2.Parse<Theatre4EventOptionTable>()
            .First(option => option.GroupId == choiceEvent.OptionGroupId && option.NextEvent is > 0
                && TableReaderV2.Parse<Theatre4EventTable>().Any(next => next.Id == option.NextEvent)
                && !(option.OptionCondition > 0) && !(option.OptionShowCondition > 0));
        Theatre4GridData chain = Theatre4EventTile(test, mapId, choiceEvent.Id, T4StateExplored);
        test.Call(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = chain.PosX, PosY = chain.PosY, Option = progression.Id });
        Theatre4GridData advanced = test.Grid(mapId, chain.PosX, chain.PosY);
        AssertEqual(progression.NextEvent!.Value, advanced.Event?.EventId ?? 0,
            "Chosen option advances the grid to its authored next event");
        Theatre4EventTable successor = TableReaderV2.Parse<Theatre4EventTable>()
            .Single(row => row.Id == progression.NextEvent.Value);
        if (successor.OptionGroupId != choiceEvent.OptionGroupId)
        {
            test.Reject(nameof(Theatre4DoGridEventRequest),
                new Theatre4DoGridEventRequest { MapId = mapId, PosX = chain.PosX, PosY = chain.PosY, Option = progression.Id },
                "Consumed option cannot be chosen again on the successor event");
        }

        // Explicit authored dialogue chain: 101 is an option-less dialogue chaining to 102, which
        // carries option group 102. Old code completed 101 immediately, ignoring its NextEvent.
        Theatre4EventTable dialogueRoot = TableReaderV2.Parse<Theatre4EventTable>().Single(row => row.Id == 101);
        AssertEqual(true, !(dialogueRoot.OptionGroupId > 0) && dialogueRoot.Type != 4 && dialogueRoot.NextEvent == 102,
            "Authored dialogue 101 is an option-less chain to 102");
        Theatre4GridData rootTile = Theatre4EventTile(test, mapId, 101, T4StateExplored);
        test.Call(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = rootTile.PosX, PosY = rootTile.PosY, Option = 0 });
        Theatre4GridData rootAfter = test.Grid(mapId, rootTile.PosX, rootTile.PosY);
        AssertEqual(102, rootAfter.Event?.EventId ?? 0,
            "Option-less dialogue 101 advances to its authored next event 102 instead of completing");
        AssertEqual(T4StateExplored, rootAfter.State, "Option-less advance keeps the tile resolvable");

        Theatre4EventOptionTable rootOption = TableReaderV2.Parse<Theatre4EventOptionTable>()
            .First(option => option.GroupId == 102 && !(option.OptionCondition > 0) && !(option.OptionShowCondition > 0));
        test.SetGold(1000);
        test.Call(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = rootTile.PosX, PosY = rootTile.PosY, Option = rootOption.Id });
        Theatre4GridData rootResolved = test.Grid(mapId, rootTile.PosX, rootTile.PosY);
        AssertEqual(true, rootResolved.State == T4StateProcessed || rootResolved.Event?.EventId != 102,
            "Dialogue 102 option resolves the authored chain");

        // Terminal authored dialogue: completes the tile with the authored reward exactly once.
        Theatre4EventTable terminal = TableReaderV2.Parse<Theatre4EventTable>()
            .First(row => !(row.OptionGroupId > 0) && !(row.NextEvent > 0) && !(row.NextEventGroup > 0)
                && row.Type != 4 && row.RewardId.Any(reward => reward > 0
                    && TableReaderV2.Parse<Theatre4RewardTable>().Any(authored => authored.Id == reward)));
        Theatre4GridData terminalTile = Theatre4EventTile(test, mapId, terminal.Id, T4StateExplored);
        int terminalBefore = test.Adventure.FinishEventIds.GetValueOrDefault(terminal.Id);
        test.Call(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = terminalTile.PosX, PosY = terminalTile.PosY, Option = 0 });
        Theatre4GridData terminalAfter = test.Grid(mapId, terminalTile.PosX, terminalTile.PosY);
        AssertEqual(T4StateProcessed, terminalAfter.State, "Terminal event completes the tile");
        AssertEqual(true, terminalAfter.Event is null, "Terminal event clears the chain id for the client");
        AssertEqual(terminalBefore + 1, test.Adventure.FinishEventIds.GetValueOrDefault(terminal.Id),
            "Terminal event records its completion exactly once");
        test.Reject(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = terminalTile.PosX, PosY = terminalTile.PosY, Option = 0 },
            "Repeated terminal completion");
        AssertEqual(terminalBefore + 1, test.Adventure.FinishEventIds.GetValueOrDefault(terminal.Id),
            "Repeated completion never re-rewards or re-records the terminal event");

        using Theatre4Case sharky = new("grid-event-sharky-accept");
        sharky.StartRun(1);
        int sharkyMap = sharky.Adventure.Chapters[0].MapId;
        Theatre4EventTable sharkyEvent = TableReaderV2.Parse<Theatre4EventTable>()
            .Single(row => row.Id == 11133 && row.OptionGroupId == 11133 && row.Name == "Sharky Box");
        Theatre4EventOptionTable accept = TableReaderV2.Parse<Theatre4EventOptionTable>()
            .Single(row => row.GroupId == sharkyEvent.OptionGroupId && row.OptionDesc == "Accept"
                && row.OptionShowCondition == 1020603 && row.NextEvent == 11140
                && !(row.OptionItemType > 0) && !(row.EffectGroupId > 0));
        Theatre4GridData sharkyTile = Theatre4EventTile(sharky, sharkyMap, sharkyEvent.Id, T4StateDiscover);
        sharky.ExploreStep(sharkyMap, sharkyTile.PosX, sharkyTile.PosY);
        AssertEqual(sharkyEvent.Id, sharky.Grid(sharkyMap, sharkyTile.PosX, sharkyTile.PosY).Event?.EventId ?? 0,
            "Exploring the authored Sharky Box leaves its Accept choice pending");
        sharky.Call(nameof(Theatre4DoGridEventRequest), new Theatre4DoGridEventRequest
        {
            MapId = sharkyMap, PosX = sharkyTile.PosX, PosY = sharkyTile.PosY, Option = accept.Id
        });
        AssertEqual(accept.NextEvent!.Value,
            sharky.Grid(sharkyMap, sharkyTile.PosX, sharkyTile.PosY).Event?.EventId ?? 0,
            "Sharky Box Accept advances to its authored successor");
        Player savedSharky = BsonSerializer.Deserialize<Player>(sharky.Players.LastSuccessfulReplacementBson!);
        AssertEqual(accept.NextEvent.Value, savedSharky.Theatre4.Data.AdventureData!.Chapters
                .Single(chapter => chapter.MapId == sharkyMap).Grids
                .Single(grid => grid.PosX == sharkyTile.PosX && grid.PosY == sharkyTile.PosY).Event?.EventId ?? 0,
            "Sharky Box Accept persists its authored successor before acknowledgement");
    }

    //endregion

    //region event-option asset effects (Type409 via the real DoGridEvent RPC)

    private static void ValidateTheatre4EventOptionEffectCompatibility()
    {
        // Authored fixtures: Event 60016 option 6001601 -> group 30006 -> effect 30013 (Division
        // 20000 on Gold); Event 60019 option 6001901 -> group 30008 -> effect 30015 (Sub 100 Gold).
        using Theatre4Case test = new("event-option-effects");
        test.StartRun(1);
        int mapId = test.Adventure.Chapters[0].MapId;

        Theatre4GridData division = Theatre4EventTile(test, mapId, 60016, T4StateExplored);
        test.SetGold(101);
        test.Reject(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = division.PosX, PosY = division.PosY, Option = int.MaxValue },
            "Unauthored event option");
        test.Call(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = division.PosX, PosY = division.PosY, Option = 6001601 });
        AssertEqual(50, test.Adventure.Gold, "Division event option halves the gold to its floor");
        test.Reject(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = division.PosX, PosY = division.PosY, Option = 6001601 },
            "Division option cannot be replayed after the chain advanced");

        Theatre4GridData subtraction = Theatre4EventTile(test, mapId, 60019, T4StateExplored);
        test.SetGold(150);
        test.Call(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = subtraction.PosX, PosY = subtraction.PosY, Option = 6001901 });
        AssertEqual(50, test.Adventure.Gold, "Subtraction event option spends its authored amount once");

        Theatre4GridData floor = Theatre4EventTile(test, mapId, 60019, T4StateExplored);
        test.SetGold(50);
        test.Call(nameof(Theatre4DoGridEventRequest),
            new Theatre4DoGridEventRequest { MapId = mapId, PosX = floor.PosX, PosY = floor.PosY, Option = 6001901 });
        AssertEqual(0, test.Adventure.Gold, "Subtraction floors a short balance to zero");

        // Both later options rebuilt the effect list; the earlier one-shot markers must hold.
        AssertEqual(1, test.Adventure.CustomEffects
            .Where(effect => effect.EffectId == 30013).Sum(effect => effect.CustomData.Count),
            "Division one-shot applies exactly once across rebuilds");
        AssertEqual(2, test.Adventure.CustomEffects
            .Where(effect => effect.EffectId == 30015).Count(effect => effect.CustomData.Count == 1),
            "Each subtraction instance applies exactly once across rebuilds");
        test.Relog("event option effects");
        AssertEqual(0, test.Adventure.Gold, "Relog never reapplies an event-option asset effect");
        AssertEqual(1, test.Adventure.CustomEffects
            .Where(effect => effect.EffectId == 30013).Sum(effect => effect.CustomData.Count),
            "Division marker survives a relog");
    }

    // Re-authors a distinct spare tile as the authored event under test. State is set directly so
    // no collateral exploration is needed and every call consumes a different tile.
    private static Theatre4GridData Theatre4EventTile(Theatre4Case test, int mapId, int eventId, int state)
    {
        Theatre4GridData host = test.Adventure.Chapters[0].Grids
            .Where(grid => grid.Type is not (T4GridBoss or T4GridStart or T4GridEvent))
            .OrderByDescending(grid => grid.PosX + grid.PosY)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Theatre4 map has no spare tile for an event fixture.");
        Theatre4GridData live = test.Grid(mapId, host.PosX, host.PosY);
        live.Type = T4GridEvent;
        live.ContentId = eventId;
        live.ContentGroup = TableReaderV2.Parse<Theatre4EventGroupTable>()
            .FirstOrDefault(row => row.EventId == eventId)?.GroupId ?? 0;
        live.Fight = null;
        live.Shop = null;
        live.Building = null;
        live.Event = new Theatre4EventData { EventId = eventId };
        live.State = state;
        test.SaveFixture();
        return live;
    }

    //endregion

    //region cross-map shop entry (Type217 key includes the map id)

    private static void ValidateTheatre4CrossMapShopEntryCompatibility()
    {
        using Theatre4Case test = new("cross-map-shop");
        test.StartRun(1);
        int firstMap = test.Adventure.Chapters[0].MapId;

        Theatre4EffectTable entry = TableReaderV2.Parse<Theatre4EffectTable>().First(row => row.Type == 217
            && row.Params.Count >= 3 && row.Params[0] == T4AssetGold);
        Theatre4SeedEffect(test, entry.Id);

        // Same coordinate on two generated authored maps; dimensions vary, so select their actual
        // overlap instead of assuming a coordinate exists.
        Theatre4ChapterData second = Theatre4InvokeInitializeMap(test, 11002);
        Theatre4GridData first = test.Chapter(firstMap).Grids
            .Where(grid => grid.Type is not (T4GridStart or T4GridBoss))
            .First(grid => second.Grids.Any(candidate => candidate.PosX == grid.PosX
                && candidate.PosY == grid.PosY && candidate.Type is not (T4GridStart or T4GridBoss)));
        int shopX = first.PosX, shopY = first.PosY;
        Theatre4GridData mirror = test.Grid(second.MapId, shopX, shopY);
        foreach (Theatre4GridData grid in new[] { first, mirror })
        {
            grid.Type = T4GridShop;
            grid.Shop = new Theatre4ShopData { ShopId = 1 };
            grid.Fight = null;
            grid.Event = null;
            grid.Building = null;
            grid.State = T4StateDiscover;
        }
        test.SaveFixture();

        while (test.Adventure.Ap < 1) test.Call(nameof(Theatre4DailySettleRequest));
        int firstGold = test.Adventure.Gold;
        test.ExploreStep(firstMap, shopX, shopY);
        AssertEqual(entry.Params[2], test.Adventure.Gold - firstGold,
            "First map's shop grants the authored entry gold");

        while (test.Adventure.Ap < 1) test.Call(nameof(Theatre4DailySettleRequest));
        int secondGold = test.Adventure.Gold;
        test.ExploreStep(second.MapId, shopX, shopY);
        AssertEqual(entry.Params[2], test.Adventure.Gold - secondGold,
            "Same-coordinate shop on a second map grants its own entry gold");

        int settledGold = test.Adventure.Gold;
        test.Reject(nameof(Theatre4ExploreGridRequest),
            new Theatre4ExploreGridRequest { MapId = second.MapId, PosX = shopX, PosY = shopY },
            "Re-entering the second shop tile");
        AssertEqual(settledGold, test.Adventure.Gold, "Re-entry never mints entry gold again");
        test.Call(nameof(Theatre4RefreshGoodsRequest),
            new Theatre4RefreshGoodsRequest { MapId = second.MapId, PosX = shopX, PosY = shopY });
        AssertEqual(true, test.Adventure.Gold <= settledGold, "Refresh never mints entry gold again");
    }

    private static Theatre4ChapterData Theatre4InvokeInitializeMap(Theatre4Case test, int mapGroupId)
    {
        object mutation = Theatre4NewMutation(test);
        Type mutationType = mutation.GetType();
        Type module = mutationType.DeclaringType!;
        object? chapter = RequiredMethod(module, "InitializeMap",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [mutationType, typeof(int)])
            .Invoke(null, [mutation, mapGroupId]);
        test.AdoptMutationState(mutation);
        return (Theatre4ChapterData?)chapter ?? throw new InvalidDataException("InitializeMap returned no chapter.");
    }

    //endregion

    //region combat (team, locate, sweep bounds, native hooks)

    private static void ValidateTheatre4CombatCompatibility()
    {
        using Theatre4Case test = new("combat");
        test.StartRun(1);
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData start = test.Adventure.Chapters[0].Grids.First(grid => grid.Type == T4GridStart);

        test.Reject(nameof(Theatre4SetTeamDataRequest), new Theatre4SetTeamDataRequest { TeamData = new() },
            "Empty team shape");
        test.Reject(nameof(Theatre4SetTeamDataRequest),
            new Theatre4SetTeamDataRequest
            {
                TeamData = new Theatre4TeamData
                {
                    CardIds = [0, 0, 0],
                    RobotIds = [int.MaxValue, 0, 0],
                    CaptainPos = 1,
                    FirstFightPos = 1
                }
            }, "Unrecruited robot roster");
        test.Reject(nameof(Theatre4FightLocateRequest),
            new Theatre4FightLocateRequest { Type = 1, MapId = mapId, PosX = start.PosX, PosY = start.PosY },
            "Fight locate on a non-fight tile");
        test.Reject(nameof(Theatre4FightLocateRequest),
            new Theatre4FightLocateRequest { Type = 99, MapId = mapId, PosX = start.PosX, PosY = start.PosY },
            "Unknown fight locate type");

        // Effect ownership is asserted from the real PreFight response: a zero-Type effect authors
        // a FightEvent id, and the affix Type33 effect accumulates explored Red-tile ids.
        Theatre4SeedEffect(test, 1001);
        Theatre4SeedEffect(test, 11000);
        foreach (Theatre4GridData grid in test.Adventure.Chapters[0].Grids)
            grid.Color = 1;
        test.SaveFixture();

        Theatre4GridData fightGrid = test.ExploreUntilFight(mapId);
        int fightX = fightGrid.PosX, fightY = fightGrid.PosY;
        AssertEqual(true, test.Grid(mapId, fightX, fightY).Fight is not null,
            "Explored fight tile carries an authored encounter");
        test.Reject(nameof(Theatre4SweepMosnterRequest),
            new Theatre4SweepMosnterRequest { MapId = mapId, PosX = fightX, PosY = fightY, SweepType = 0 },
            "Unsupported sweep type");


        Theatre4TeamData locatedTeam = test.RecruitTeam(2);
        test.Call(nameof(Theatre4FightLocateRequest),
            new Theatre4FightLocateRequest { Type = 1, MapId = mapId, PosX = fightX, PosY = fightY });
        AssertEqual(true, test.State.ActiveEncounter is { PreFightPayload: null },
            "Fight locate freezes an encounter before battle authorization");
        test.Call(nameof(Theatre4SetTeamDataRequest), new Theatre4SetTeamDataRequest { TeamData = locatedTeam });
        AssertEqual(true, test.Adventure.TeamData!.RobotIds.Count(id => id > 0) == 2
                && test.Adventure.TeamData.RobotIds.Count(id => id == 0) == 1,
            "Located encounter accepts two recruited robots and one empty slot");
        AssertEqual(true, test.State.ActiveEncounter is not null, "Fight locate freezes the encounter");
        byte[] frozenEncounter = test.State.ActiveEncounter!.ToBson();
        test.Reject(nameof(Theatre4SweepMosnterRequest),
            new Theatre4SweepMosnterRequest { MapId = mapId, PosX = fightX, PosY = fightY, SweepType = 2 },
            "Sweep while a battle is frozen");
        test.Relog("frozen encounter", pending: true);
        AssertEqual(true, test.State.ActiveEncounter is not null, "Frozen encounter survives a relog");
        AssertEqual(true, frozenEncounter.SequenceEqual(test.State.ActiveEncounter!.ToBson()),
            "Relog never rerolls the frozen encounter");

        JObject prepared = test.AuthorizeFrozenFight();
        test.Reject(nameof(Theatre4SetTeamDataRequest),
            new Theatre4SetTeamDataRequest
            {
                TeamData = new Theatre4TeamData
                {
                    CardIds = [0, 0, 0],
                    RobotIds = [locatedTeam.RobotIds[1], locatedTeam.RobotIds[0], 0],
                    CaptainPos = 1,
                    FirstFightPos = 1
                }
            }, "Team change after native PreFight authorization");
        AssertEqual(true, Theatre4JsonListContains(prepared!, "EventIds", 2251500),
            "Zero-Type effect projects its authored FightEvent id into the player events");
        AssertEqual(true, Theatre4JsonListContains(prepared!, "EventIds", 2251650),
            "Type33 accumulation projects the explored Red-tile FightEvent id");
        AssertEqual(false, Theatre4JsonListContains(prepared!, "EventIds", 11000),
            "Effect ids are never projected as FightEvent ids");
        AssertEqual(false, Theatre4JsonMapContainsKey(prepared!, "MagicIds", 2251500),
            "FightEvent ids are never reinterpreted as magic ids");
        test.ResolveFrozenFight();
        AssertEqual(true, test.Adventure.FinishFightIds.Count > 0, "Native settlement records the finished fight");
        AssertEqual(true, test.State.ActiveEncounter is { SettleReceipt: not null },
            "Native settlement freezes its receipt for duplicate transports");
        test.ResolveFrozenFight();

        // An authored Type-4 event fight whose fight row carries no reward drop: winning it must
        // still authorize and settle through the native path, advance the event chain, and never
        // invent a fight-reward transaction. Event rewards live on the chain rows, not the battle.
        (Theatre4EventTable fightEvent, Theatre4FightTable fightRow, int fightStage) = Theatre4RewardlessEventFight();
        using Theatre4Case eventFight = new("event-fight-no-drop");
        eventFight.StartRun(1);
        int eventMapId = eventFight.Adventure.Chapters[0].MapId;
        Theatre4GridData eventTile = eventFight.ExploreToward(eventMapId, Theatre4GridOfType(eventFight, T4GridEvent));
        eventTile.Event = new Theatre4EventData { EventId = fightEvent.Id, StageId = fightStage };
        eventFight.SaveFixture();
        eventFight.RecruitAndSetTeam();
        eventFight.Call(nameof(Theatre4FightLocateRequest),
            new Theatre4FightLocateRequest { Type = 1, MapId = eventMapId, PosX = eventTile.PosX, PosY = eventTile.PosY });
        AssertEqual(fightRow.Id, eventFight.State.ActiveEncounter!.FightId,
            "Reward-less event fight freezes the authored encounter");
        eventFight.ResolveFrozenFight();
        Theatre4GridData settledEvent = eventFight.Grid(eventMapId, eventTile.PosX, eventTile.PosY);
        List<int> authoredSuccessors = fightEvent.NextEvent is > 0
            ? [fightEvent.NextEvent.Value]
            : TableReaderV2.Parse<Theatre4EventGroupTable>()
                .Where(row => row.GroupId == fightEvent.NextEventGroup)
                .Select(row => row.EventId).Where(id => id > 0).ToList();
        if (authoredSuccessors.Count == 0)
        {
            AssertEqual(T4StateProcessed, settledEvent.State, "Reward-less event fight completes its authored chain");
            AssertEqual(true, settledEvent.Event is null, "Reward-less event fight closes its terminal chain");
        }
        else
        {
            AssertEqual(T4StateExplored, settledEvent.State, "Reward-less event fight keeps its continuing chain open");
            AssertEqual(true, settledEvent.Event is { } next && authoredSuccessors.Contains(next.EventId),
                "Reward-less event fight advances to an authored successor");
        }
        AssertEqual(true, eventFight.Adventure.FinishEventIds.ContainsKey(fightEvent.Id),
            "Reward-less event fight records the finished event");
        AssertEqual(true, eventFight.Adventure.Transactions.All(transaction => transaction.Type != 4
                || transaction.ConfigId != fightRow.Id),
            "Reward-less event fight invents no fight-reward transaction");
    }

    private static void ValidateTheatre4SignedCountdownCompatibility()
    {
        (Theatre4EventTable authoredEvent, _, int stageId) = Theatre4UntimedEventFight();
        foreach ((bool win, long leftTime) in new[] { (true, -37L), (false, -41L) })
        {
            using Theatre4Case test = new($"signed-countdown-{win}");
            test.StartRun(1);
            int mapId = test.Adventure.Chapters[0].MapId;
            Theatre4GridData tile = test.ExploreToward(mapId, Theatre4GridOfType(test, T4GridEvent));
            tile.Event = new Theatre4EventData { EventId = authoredEvent.Id, StageId = stageId };
            test.SaveFixture();
            test.RecruitAndSetTeam();
            test.Call(nameof(Theatre4FightLocateRequest),
                new Theatre4FightLocateRequest { Type = 1, MapId = mapId, PosX = tile.PosX, PosY = tile.PosY });
            test.AuthorizeFrozenFight();
            Theatre4ActiveEncounter encounter = test.State.ActiveEncounter!;

            if (!win)
            {
                FightSettleResult invalid = Theatre4Case.SettleReport(encounter, leftTime, win);
                invalid.StageId++;
                test.Reject(nameof(FightSettleRequest), new FightSettleRequest { Result = invalid },
                    "Mismatched signed-countdown stage");
                invalid.StageId--;
                invalid.LeftTime = (long)int.MinValue - 1;
                test.Reject(nameof(FightSettleRequest), new FightSettleRequest { Result = invalid },
                    "Signed countdown outside Int32");
                invalid.LeftTime = leftTime;
                invalid.SettleFrame = -1;
                test.Reject(nameof(FightSettleRequest), new FightSettleRequest { Result = invalid },
                    "Malformed signed-countdown report");
            }

            int hp = test.Adventure.Hp;
            FightSettleResult report = Theatre4Case.SettleReport(encounter, leftTime, win);
            JObject settled = test.Call(nameof(FightSettleRequest), new FightSettleRequest { Result = report },
                verifyPersistence: false);
            AssertEqual(0, settled.Value<int>("Code"), $"Signed countdown {(win ? "win" : "loss")} settles");
            AssertEqual(leftTime, settled["Settle"]!.Value<long>("LeftTime"),
                $"Signed countdown {(win ? "win" : "loss")} preserves its native value");
            AssertEqual(hp - (win ? 0 : 1), test.Adventure.Hp,
                $"Signed countdown {(win ? "win" : "loss")} applies expected HP");
            AssertEqual(win, test.Adventure.FinishEventIds.ContainsKey(authoredEvent.Id),
                $"Signed countdown {(win ? "win" : "loss")} applies expected progression");

            int committedHp = test.Adventure.Hp;
            int committedGold = test.Adventure.Gold;
            int committedTransactions = test.Adventure.Transactions.Count;
            Dictionary<int, long> committedItems = test.ItemCounts();
            void AssertNotRegranted(string name)
            {
                AssertEqual(committedHp, test.Adventure.Hp, $"{name} preserves HP");
                AssertEqual(committedGold, test.Adventure.Gold, $"{name} preserves gold");
                AssertEqual(committedTransactions, test.Adventure.Transactions.Count,
                    $"{name} cannot add another reward offer");
                AssertEqual(win, test.Adventure.FinishEventIds.ContainsKey(authoredEvent.Id),
                    $"{name} cannot change event progression");
                AssertEqual(true, committedItems.OrderBy(item => item.Key)
                    .SequenceEqual(test.ItemCounts().OrderBy(item => item.Key)),
                    $"{name} cannot regrant account items");
            }
            JObject duplicate = test.Call(nameof(FightSettleRequest), new FightSettleRequest { Result = report },
                verifyPersistence: false);
            AssertEqual(leftTime, duplicate["Settle"]!.Value<long>("LeftTime"),
                "Duplicate signed countdown replays its receipt");
            AssertNotRegranted("Duplicate signed countdown receipt");
            test.Relog($"signed countdown {win}");
            test.Call(nameof(FightSettleRequest), new FightSettleRequest { Result = report }, verifyPersistence: false);
            AssertNotRegranted("Relogged signed countdown receipt");
        }
    }

    //endregion

    //region complete run

    private static void ValidateTheatre4CompleteRunCompatibility()
    {
        using Theatre4Case test = new("complete-run");
        test.StartRun(1);
        AssertEqual(true, test.CompleteRun(), "Beginner route is completable with the authored resources");
        AssertEqual(true, test.Data.AdventureData is null, "Winning the final boss closes the adventure");
        AssertEqual(true, test.Data.Difficultys.GetValueOrDefault(1) >= 1,
            "Winning records the permanent difficulty clear");
        AssertEqual(true, test.Data.Endings.Values.Sum() >= 1, "Winning records a permanent ending");
        AssertEqual(true, test.Data.PreAdventureSettleData is { SettleType: 2 },
            "Route end settles as a route completion");
        NotifyTheatre4AdventureSettle win = test.EndingPush("win");
        AssertEndingShape(win, 2, 1, "Winning ending");
        AssertEqual(true, win.AdventureData!.SettleBpExp > 0, "Winning terminal snapshot carries the authored BP reward");

        NotifyTheatre4AdventureSettle winRecovered = test.RelogEnding("completed run");
        AssertEndingShape(winRecovered, 2, 1, "Winning relog ending");
        AssertEqual(true, test.Data.AdventureData is null, "Completed run stays closed after login");
        AssertEqual(true, MessagePackSerializer.Serialize(win.AdventureData).SequenceEqual(
            MessagePackSerializer.Serialize(winRecovered.AdventureData)),
            "Completed ending survives relog with the same terminal snapshot");

        // The permanent clear satisfies the authored difficulty-2 gate (Condition 17410 Difficultys[1]).
        test.StartRun(2);
        AssertEqual(2, test.Adventure.Difficulty, "Second run selects the newly unlocked difficulty");
        AssertEqual(2, test.Adventure.MapBlueprintId, "Difficulty 2 uses the authored coloured route");
        AssertEqual(41001, test.Adventure.Chapters[0].MapGroup, "Difficulty 2 opens the authored MapGroup 41001");
        test.Call(nameof(Theatre4SettleAdventureRequest));
        AssertEqual(true, test.Data.AdventureData is null, "Second run abandons cleanly");
        NotifyTheatre4AdventureSettle secondAbandon = test.EndingPush("second abandon");
        AssertEndingShape(secondAbandon, 3, 2, "Second run abandon ending");
        AssertEqual(true, win.SettleData!.EndingId != secondAbandon.SettleData!.EndingId,
            "Distinct terminal outcomes resolve distinct authored endings");
    }

    //endregion
    //region continuation regression (explicit map continuation after native combat)

    private static void ValidateTheatre4ContinuationCompatibility()
    {
        // Opening a battle room without a native authorization is a map-only transition: Enter
        // must not charge a life, create a reward offer, or consume the unresolved locate.
        using (Theatre4Case room = new("continuation-prelocate"))
        {
            room.StartRun(1);
            int mapId = room.Adventure.Chapters[0].MapId;
            Theatre4GridData target = Theatre4ContinuationFightTarget(room, mapId);
            int hp = room.Adventure.Hp;
            int rewards = room.Adventure.Transactions.Count(transaction => transaction.Type == 4);

            room.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = 1, MapId = mapId, PosX = target.PosX, PosY = target.PosY
            });
            AssertEqual(true, room.State.ActiveEncounter is { PreFightPayload: null, SettleReceipt: null },
                "Locate-only room is frozen without native authorization");
            room.Call(nameof(Theatre4EnterRequest));
            AssertEqual(hp, room.Adventure.Hp, "Locate-only Enter does not charge HP");
            AssertEqual(rewards, room.Adventure.Transactions.Count(transaction => transaction.Type == 4),
                "Locate-only Enter does not create a fight reward");
            AssertEqual(true, room.State.ActiveEncounter is { PreFightPayload: null, SettleReceipt: null },
                "Locate-only Enter leaves the unresolved room uncharged");
        }

        // Native UI stores NONESELECT as 9999, while EN XFubenAgency.NetWorkPreFightRequest
        // sends the independent wire value 0 (XEnumConst.lua:445, XFubenAgency.lua:4208-4210).
        using (Theatre4Case skill = new("continuation-general-skill"))
        {
            const int nativeUiNoSelection = 9999;
            skill.StartRun(1);
            int mapId = skill.Adventure.Chapters[0].MapId;
            Theatre4GridData target = Theatre4ContinuationFightTarget(skill, mapId);
            Theatre4TeamData team = skill.RecruitTeam(2);
            team.GeneralSkill = nativeUiNoSelection;
            skill.Call(nameof(Theatre4SetTeamDataRequest),
                new Theatre4SetTeamDataRequest { TeamData = team });

            skill.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = 1, MapId = mapId, PosX = target.PosX, PosY = target.PosY
            });
            Theatre4ActiveEncounter located = skill.State.ActiveEncounter
                ?? throw new InvalidDataException("General-skill probe did not freeze its located fight.");
            PreFightRequest wireNoSelection = new()
            {
                PreFightData = new()
                {
                    ChallengeCount = 1,
                    StageId = checked((uint)located.StageId),
                    CardIds = team.CardIds.Select(id => checked((uint)id)).ToList(),
                    RobotIds = team.RobotIds.ToList(),
                    CaptainPos = team.CaptainPos,
                    FirstFightPos = team.FirstFightPos,
                    EnterCgIndex = team.EnterCgIndex,
                    SettleCgIndex = team.SettleCgIndex,
                    // Explicit native wire contract; do not derive this from stored TeamData.
                    GeneralSkill = 0
                }
            };
            JObject prepared = skill.Call(nameof(PreFightRequest), wireNoSelection,
                verifyPersistence: false);
            PreFightResponse preparedResponse =
                MessagePackSerializer.Deserialize<PreFightResponse>(skill.LastResponseContent);
            AssertEqual(0, preparedResponse.Code, "Native wire no-selection PreFight succeeds");
            AssertEqual(true, preparedResponse.FightData.FightId > 0,
                "Native wire no-selection returns FightData");
            AssertEqual(nativeUiNoSelection, skill.Adventure.TeamData!.GeneralSkill,
                "Successful PreFight preserves the stored UI sentinel");
            byte[] frozenAuthorized = skill.State.ActiveEncounter!.ToBson();

            JObject retry = skill.Call(nameof(PreFightRequest), wireNoSelection,
                verifyPersistence: false);
            AssertEqual(true, JToken.DeepEquals(prepared["FightData"], retry["FightData"]),
                "Equivalent GeneralSkill-0 retry preserves frozen FightData identity");
            AssertEqual(true, frozenAuthorized.SequenceEqual(skill.State.ActiveEncounter!.ToBson()),
                "Equivalent GeneralSkill-0 retry preserves the frozen encounter snapshot");

            byte[] beforeConflict = skill.State.ToBson();
            PreFightRequest conflicting = MessagePackSerializer.Deserialize<PreFightRequest>(
                MessagePackSerializer.Serialize(wireNoSelection));
            conflicting.PreFightData.GeneralSkill = nativeUiNoSelection - 1;
            skill.Reject(nameof(PreFightRequest), conflicting,
                "Nonzero conflicting general skill");
            AssertEqual(true, beforeConflict.SequenceEqual(skill.State.ToBson()),
                "Conflicting general skill leaves the Theatre4 snapshot unchanged");
        }

        using (Theatre4Case test = new("continuation-authorized"))
        {
            test.StartRun(1);
            while (test.Adventure.Transactions.FirstOrDefault(transaction => transaction.Type == 1) is { } recruit)
                test.Call(nameof(Theatre4ConfirmRecruitRequest),
                    new Theatre4ConfirmRecruitRequest { TransactionId = recruit.Id, Index = 1 });
            int mapId = test.Adventure.Chapters[0].MapId;
            Theatre4GridData first = Theatre4ContinuationFightTarget(test, mapId);
            // Make the alternate target visible before authorizing the first battle; the active
            // native room must block exploration, while Enter must then permit this target.
            Theatre4GridData second = Theatre4ContinuationFightTarget(test, mapId, first.GridId);
            int secondX = second.PosX, secondY = second.PosY, secondFightId = second.ContentId;
            Theatre4TeamData team = test.RecruitTeam(2);

            test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = 1, MapId = mapId, PosX = first.PosX, PosY = first.PosY
            });
            test.Call(nameof(Theatre4SetTeamDataRequest), new Theatre4SetTeamDataRequest { TeamData = team });
            JObject prepared = test.AuthorizeFrozenFight();
            byte[] frozenPreFight = test.State.ActiveEncounter!.PreFightPayload
                ?? throw new InvalidDataException("Continuation PreFight did not freeze its payload.");

            // A malformed native report must be rejected while preserving the authorized battle
            // for retry, not converted into a leave or a consumed settlement.
            Theatre4ActiveEncounter authorized = test.State.ActiveEncounter;
            FightSettleResult malformed = Theatre4Case.SettleReport(authorized);
            malformed.FightId++;
            int hpBeforeMalformed = test.Adventure.Hp;
            int finishedBeforeMalformed = test.Adventure.FinishFightIds.Count;
            int rewardsBeforeMalformed = test.Adventure.Transactions.Count(transaction => transaction.Type == 4);
            JObject rejected = test.Call(nameof(FightSettleRequest),
                new FightSettleRequest { Result = malformed }, success: false);
            AssertEqual(true, rejected.Value<int>("Code") != 0, "Malformed settle is rejected");
            AssertEqual(hpBeforeMalformed, test.Adventure.Hp, "Malformed settle preserves HP");
            AssertEqual(finishedBeforeMalformed, test.Adventure.FinishFightIds.Count,
                "Malformed settle preserves map progression");
            AssertEqual(rewardsBeforeMalformed,
                test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
                "Malformed settle preserves reward offers");
            AssertEqual(true, test.State.ActiveEncounter is { PreFightPayload: not null, SettleReceipt: null },
                "Malformed settle leaves the authorized encounter retryable");

            // Login must keep the frozen native plan. The explicit Enter below is the first
            // continuation transition; native reconnect is intentionally not that transition.
            test.Relog("continuation-authorized", pending: true);
            AssertEqual(true, frozenPreFight.SequenceEqual(test.State.ActiveEncounter!.PreFightPayload!),
                "Relog preserves the authorized PreFight bytes");
            JObject replay = test.AuthorizeFrozenFight();
            AssertEqual(true, JToken.DeepEquals(prepared["FightData"], replay["FightData"]),
                "PreFight retry before Enter replays the frozen response");

            int hpBeforeEnter = test.Adventure.Hp;
            int finishedBeforeEnter = test.Adventure.FinishFightIds.Count;
            int rewardsBeforeEnter = test.Adventure.Transactions.Count(transaction => transaction.Type == 4);
            test.Call(nameof(Theatre4EnterRequest));

            // Keep this locate before the HP assertion: the preserved old runtime reports the
            // original active-battle 20218121 here, while the fixed runtime allows continuation.
            test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = 1, MapId = mapId, PosX = secondX, PosY = secondY
            });
            AssertEqual(hpBeforeEnter - 1, test.Adventure.Hp,
                "Authorized Enter applies exactly one HP loss");
            AssertEqual(finishedBeforeEnter, test.Adventure.FinishFightIds.Count,
                "Authorized Enter does not advance the map");
            AssertEqual(rewardsBeforeEnter, test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
                "Authorized Enter does not create a reward offer");
            AssertEqual(null, test.Session.fight, "Authorized Enter clears the matching native session fight");

            test.Call(nameof(Theatre4SetTeamDataRequest), new Theatre4SetTeamDataRequest { TeamData = team });
            test.AuthorizeFrozenFight();
            Theatre4ActiveEncounter secondEncounter = test.State.ActiveEncounter;
            int hpBeforeWin = test.Adventure.Hp;
            int finishedBeforeWin = test.Adventure.FinishFightIds.Count;
            int rewardsBeforeWin = test.Adventure.Transactions.Count(transaction => transaction.Type == 4);
            FightSettleResult valid = Theatre4Case.SettleReport(secondEncounter);
            JObject settled = test.Call(nameof(FightSettleRequest),
                new FightSettleRequest { Result = valid }, verifyPersistence: false);
            AssertEqual(0, settled.Value<int>("Code"), "Continuation target settles through the native packet");
            AssertEqual(hpBeforeWin, test.Adventure.Hp, "Valid win does not charge HP");
            AssertEqual(finishedBeforeWin + 1, test.Adventure.FinishFightIds.Count,
                "Valid win advances exactly one map fight");
            AssertEqual(true, test.Adventure.FinishFightIds.Contains(secondFightId),
                "Valid win records the selected target");
            AssertEqual(rewardsBeforeWin + 1,
                test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
                "Valid win creates its authored fight reward offer");
            AssertEqual(true, test.State.ActiveEncounter is { SettleReceipt: not null },
                "Valid win freezes a settlement receipt");
            byte[] liveAfterWin = MessagePackSerializer.Serialize(test.Adventure);
            test.Reject(nameof(Theatre4SelectDifficultRequest),
                new Theatre4SelectDifficultRequest { Difficult = test.Adventure.Difficulty },
                "Ordinary win cannot start another run");
            AssertEqual(true, liveAfterWin.SequenceEqual(MessagePackSerializer.Serialize(test.Adventure)),
                "Rejected next difficulty preserves the ordinary winning run");

            int finishedAfterWin = test.Adventure.FinishFightIds.Count;
            int rewardsAfterWin = test.Adventure.Transactions.Count(transaction => transaction.Type == 4);
            test.Call(nameof(FightSettleRequest), new FightSettleRequest { Result = valid },
                verifyPersistence: false);
            AssertEqual(finishedAfterWin, test.Adventure.FinishFightIds.Count,
                "Settlement retry cannot advance the map twice");
            AssertEqual(rewardsAfterWin, test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
                "Settlement retry cannot create a second reward offer");
            test.Relog("continuation-settled");
            test.Call(nameof(FightSettleRequest), new FightSettleRequest { Result = valid },
                verifyPersistence: false);
            AssertEqual(finishedAfterWin, test.Adventure.FinishFightIds.Count,
                "Relogged settlement replay cannot advance the map twice");
            AssertEqual(rewardsAfterWin, test.Adventure.Transactions.Count(transaction => transaction.Type == 4),
                "Relogged settlement replay cannot create a second reward offer");
        }

        using (Theatre4Case terminal = new("continuation-last-hp"))
        {
            terminal.StartRun(1);
            int mapId = terminal.Adventure.Chapters[0].MapId;
            Theatre4GridData target = Theatre4ContinuationFightTarget(terminal, mapId);
            terminal.Adventure.Hp = 1;
            terminal.SaveFixture();
            Theatre4TeamData team = terminal.RecruitTeam(2);
            terminal.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
            {
                Type = 1, MapId = mapId, PosX = target.PosX, PosY = target.PosY
            });
            terminal.Call(nameof(Theatre4SetTeamDataRequest), new Theatre4SetTeamDataRequest { TeamData = team });
            terminal.AuthorizeFrozenFight();
            int endingsBefore = terminal.Data.Endings.Values.Sum();

            terminal.Call(nameof(Theatre4EnterRequest));
            AssertEqual(null, terminal.Data.AdventureData,
                "Last HP Enter closes the client adventure");
            AssertEqual(null, terminal.State.ActiveEncounter,
                "Last HP terminal Enter clears the active encounter");
            AssertEqual(true, terminal.State.PendingSettleAdventure is not null,
                "Last HP terminal Enter retains its terminal snapshot");

            int settleIndex = terminal.Pushes.FindIndex(push => push.Name == nameof(NotifyTheatre4AdventureSettle));
            int activityIndex = terminal.Pushes.FindIndex(push => push.Name == nameof(NotifyTheatre4ActivityData));
            AssertEqual(true, settleIndex >= 0 && activityIndex > settleIndex,
                "Terminal activity clears the map before EnterResponse");
            NotifyTheatre4ActivityData activity = MessagePackSerializer.Deserialize<NotifyTheatre4ActivityData>(
                terminal.Pushes[activityIndex].Content);
            AssertEqual(null, activity.Data.AdventureData,
                "Terminal activity packet carries no stale adventure");
            AssertEqual(1, terminal.Pushes.Count(push => push.Name == nameof(NotifyTheatre4AdventureSettle)),
                "Last HP Enter emits one terminal settlement");

            terminal.Reject(nameof(Theatre4EnterRequest), null, "Terminal Enter retry");
            AssertEqual(endingsBefore, terminal.Data.Endings.Values.Sum(),
                "Terminal failure retry cannot unlock a successful ending");
            terminal.RelogEnding("continuation-last-hp");
            AssertEqual(endingsBefore, terminal.Data.Endings.Values.Sum(),
                "Terminal failure relog cannot unlock a successful ending");
        }
    }

    // Same-session journal recovery is the only callback replay the native contract exposes:
    // a failed final player write keeps the terminal outcome frozen for the identical semantic
    // FightSettle request, while a fresh login presents the terminal ending with no battle.
    private static void ValidateTheatre4FightSettlementJournalCompatibility()
    {
        using (Theatre4Case retry = new("fight-settlement-journal-retry"))
        {
            FightSettleRequest request = Theatre4LastHpSettleRequest(retry, out int endingsBefore);
            int difficultyBefore = retry.Data.Difficultys.GetValueOrDefault(1);
            int failedPacket = retry.NextPacketId;
            retry.Players.BeforeReplaceOne = replacement =>
            {
                if (replacement.Theatre4.PendingMutation is null)
                    throw new MongoDB.Driver.MongoException("Injected Theatre4 final settlement save failure.");
            };
            try
            {
                JObject failed = retry.Call(nameof(FightSettleRequest), request, success: false,
                    verifyPersistence: false);
                AssertEqual(true, failed.Value<int>("Code") != 0,
                    "Final settlement player-save failure is reported");
            }
            finally
            {
                retry.Players.BeforeReplaceOne = null;
            }
            AssertEqual(true, retry.State.PendingMutation is not null,
                "Final settlement save failure retains the in-memory journal");
            Player durablePending = BsonSerializer.Deserialize<Player>(retry.Players.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Final settlement failure did not persist its journal."));
            AssertEqual(true, durablePending.Theatre4.PendingMutation is not null,
                "Final settlement save failure retains a durable journal");
            AssertEqual(null, retry.State.PendingMutation!.Outcome.ActiveEncounter,
                "Terminal settlement journal cannot resurrect an active encounter");
            AssertEqual(null, durablePending.Theatre4.PendingMutation!.Outcome.ActiveEncounter,
                "Durable terminal settlement journal has no active encounter");

            int retryPacket = retry.NextPacketId;
            JObject recovered = retry.Call(nameof(FightSettleRequest), request, verifyPersistence: true);
            byte[] recoveredBytes = retry.LastResponseContent.ToArray();
            AssertEqual(0, recovered.Value<int>("Code"), "Same-session settlement journal retry succeeds");
            AssertEqual(true, retryPacket != failedPacket, "Settlement retry allocates a new transport id");
            AssertEqual(null, retry.State.PendingMutation, "Successful settlement retry clears the journal");
            AssertEqual(null, retry.State.ActiveEncounter, "Successful terminal retry keeps battle closed");
            AssertEqual(null, retry.Data.AdventureData, "Successful terminal retry closes the adventure");
            AssertEqual(endingsBefore, retry.Data.Endings.Values.Sum(),
                "Successful terminal retry does not record a failed ending");
            AssertEqual(difficultyBefore, retry.Data.Difficultys.GetValueOrDefault(1),
                "Successful terminal retry does not record a difficulty clear");
            AssertEqual(true, retry.State.PendingSettleAdventure is not null,
                "Successful terminal retry retains the failed terminal presentation");
            AssertEqual(1, retry.Pushes.Count(push => push.Name == nameof(NotifyTheatre4AdventureSettle)),
                "Successful settlement retry sends one owed failed terminal presentation");
            NotifyTheatre4AdventureSettle retryEnding = retry.EndingPush("settlement retry");
            AssertEndingShape(retryEnding, 1, 1, "Settlement retry failure");
            Dictionary<int, long> recoveredItems = retry.ItemCounts();

            retry.Call(nameof(FightSettleRequest), request, verifyPersistence: false,
                transportId: retryPacket);
            AssertEqual(true, recoveredBytes.SequenceEqual(retry.LastResponseContent),
                "Committed settlement receipt replays exact response bytes");
            AssertEqual(0, retry.Pushes.Count,
                "Committed settlement receipt replay emits no owed pushes twice");
            AssertEqual(endingsBefore, retry.Data.Endings.Values.Sum(),
                "Committed failed settlement receipt does not record an ending twice");
            AssertEqual(difficultyBefore, retry.Data.Difficultys.GetValueOrDefault(1),
                "Committed failed settlement receipt does not clear a difficulty twice");
            AssertEqual(true, recoveredItems.OrderBy(pair => pair.Key)
                .SequenceEqual(retry.ItemCounts().OrderBy(pair => pair.Key)),
                "Committed failed settlement receipt replay emits no reward credits twice");
        }

        using (Theatre4Case initialSave = new("fight-settlement-journal-initial-save"))
        {
            FightSettleRequest request = Theatre4LastHpSettleRequest(initialSave, out int endingsBefore);
            int difficultyBefore = initialSave.Data.Difficultys.GetValueOrDefault(1);
            int failedPacket = initialSave.NextPacketId;
            initialSave.Players.ThrowOnReplaceOne = true;
            try
            {
                _ = initialSave.Call(nameof(FightSettleRequest), request, success: false,
                    verifyPersistence: false);
            }
            finally
            {
                initialSave.Players.ThrowOnReplaceOne = false;
            }
            AssertEqual(0, initialSave.Pushes.Count,
                "Initial journal save failure emits no success pushes");
            AssertEqual(true, initialSave.State.PendingMutation is not null,
                "Initial journal save failure keeps the frozen mutation in memory");
            AssertEqual(null, initialSave.State.PendingMutation!.Outcome.ActiveEncounter,
                "Initial journal save failure keeps the terminal outcome battle-free");
            Player preOperation = BsonSerializer.Deserialize<Player>(initialSave.Players.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Initial journal failure lost the previous player snapshot."));
            AssertEqual(null, preOperation.Theatre4.PendingMutation,
                "Initial journal save failure leaves BSON at the pre-operation snapshot");
            int retryPacket = initialSave.NextPacketId;
            JObject recovered = initialSave.Call(nameof(FightSettleRequest), request, verifyPersistence: true);
            AssertEqual(0, recovered.Value<int>("Code"),
                "Initial journal save retry completes the frozen terminal outcome");
            AssertEqual(true, retryPacket != failedPacket,
                "Initial journal retry allocates a new transport id");
            AssertEqual(null, initialSave.State.PendingMutation,
                "Initial journal retry clears the frozen mutation");
            AssertEqual(null, initialSave.Data.AdventureData,
                "Initial journal retry closes the terminal adventure");
            AssertEqual(endingsBefore, initialSave.Data.Endings.Values.Sum(),
                "Initial journal retry does not record a failed ending");
            AssertEqual(difficultyBefore, initialSave.Data.Difficultys.GetValueOrDefault(1),
                "Initial journal retry does not record a difficulty clear");
            AssertEqual(true, initialSave.State.PendingSettleAdventure is not null,
                "Initial journal retry retains the failed terminal presentation");
            AssertEqual(1, initialSave.Pushes.Count(push => push.Name == nameof(NotifyTheatre4AdventureSettle)),
                "Initial journal retry sends one failed terminal presentation");
            NotifyTheatre4AdventureSettle initialEnding = initialSave.EndingPush("initial journal retry");
            AssertEndingShape(initialEnding, 1, 1, "Initial journal retry failure");
        }


        using (Theatre4Case freshLogin = new("fight-settlement-journal-login"))
        {
            FightSettleRequest request = Theatre4LastHpSettleRequest(freshLogin, out int endingsBefore);
            int difficultyBefore = freshLogin.Data.Difficultys.GetValueOrDefault(1);
            freshLogin.Players.BeforeReplaceOne = replacement =>
            {
                if (replacement.Theatre4.PendingMutation is null)
                    throw new MongoDB.Driver.MongoException("Injected Theatre4 final settlement login failure.");
            };
            try
            {
                _ = freshLogin.Call(nameof(FightSettleRequest), request, success: false,
                    verifyPersistence: false);
            }
            finally
            {
                freshLogin.Players.BeforeReplaceOne = null;
            }
            Dictionary<int, long> itemsBeforeRelog = freshLogin.ItemCounts();
            freshLogin.Relog("fight settlement journal login", pending: true);
            AssertEqual(true, itemsBeforeRelog.OrderBy(pair => pair.Key)
                .SequenceEqual(freshLogin.ItemCounts().OrderBy(pair => pair.Key)),
                "Fresh login terminal recovery preserves consolation rewards without regranting");
            AssertEqual(null, freshLogin.State.PendingMutation,
                "Fresh login completes the durable terminal journal");
            AssertEqual(null, freshLogin.State.ActiveEncounter,
                "Fresh login does not resurrect the terminal battle");
            AssertEqual(null, freshLogin.Data.AdventureData,
                "Fresh login presents a closed adventure after terminal recovery");
            AssertEqual(endingsBefore, freshLogin.Data.Endings.Values.Sum(),
                "Fresh login terminal recovery does not record a failed ending");
            AssertEqual(difficultyBefore, freshLogin.Data.Difficultys.GetValueOrDefault(1),
                "Fresh login terminal recovery does not record a difficulty clear");
            AssertEqual(true, freshLogin.State.PendingSettleAdventure is not null,
                "Fresh login terminal recovery retains the failed terminal presentation");
            freshLogin.PublishRecoveredEnding();
            NotifyTheatre4AdventureSettle ending = ReadPushPayload<NotifyTheatre4AdventureSettle>(
                freshLogin.Harness, nameof(NotifyTheatre4AdventureSettle),
                "Fresh login terminal settlement recovery");
            AssertEndingShape(ending, 1, 1, "Fresh login terminal recovery failure");
            AssertEqual(false, freshLogin.Harness.TryReadAvailablePacket("duplicate terminal recovery", out _),
                "Fresh login terminal recovery publishes no duplicate presentation");
            AssertEqual(true, itemsBeforeRelog.OrderBy(pair => pair.Key)
                .SequenceEqual(freshLogin.ItemCounts().OrderBy(pair => pair.Key)),
                "Fresh login terminal presentation emits no duplicate reward credits");
            AssertEqual(null, freshLogin.State.ActiveEncounter,
                "Terminal settlement push leaves no active encounter");
        }
    }

    //region development settlement regression

    private static void ValidateTheatre4DevelopmentCompatibility()
    {
        using Theatre4Case test = new("development");
        test.StartRun(1);

        // The registered map/combat/reward path supplies real colour resources before the tally.
        test.Adventure.Ap = 100;
        test.SaveFixture();
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData firstFight = Theatre4ContinuationFightTarget(test, mapId);
        test.RecruitAndSetTeam();
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = 1, MapId = mapId, PosX = firstFight.PosX, PosY = firstFight.PosY
        });
        test.ResolveFrozenFight();
        Theatre4TransactionData reward = test.Adventure.Transactions
            .Single(transaction => transaction.Type == 4);
        AssertEqual(true, reward.Rewards.Count > 0, "Fight reward remains a selectable offer");
        int rewardIndex = 1;
        test.Call(nameof(Theatre4ConfirmFightRewardRequest),
            new Theatre4ConfirmFightRewardRequest { TransactionId = reward.Id, Index = rewardIndex });
        test.RepeatRejected(nameof(Theatre4ConfirmFightRewardRequest),
            new Theatre4ConfirmFightRewardRequest { TransactionId = reward.Id, Index = rewardIndex },
            "Fight reward selection replay");

        // Union is authored item 18 / effect 18: +5000 markup per owned blueprint, i.e. x1.5
        // with this one-item fixture. DailyResource is an independent earned rate.
        Theatre4SeedItem(test, 18);
        foreach (Theatre4ColorTalentData color in test.Adventure.Colors)
            Theatre4AddAsset(test, T4AssetColorDailyResource, color.Color, 7);

        List<Theatre4ColorTalentSlotTable> slots = TableReaderV2.Parse<Theatre4ColorTalentSlotTable>();
        Theatre4ColorTalentData red = test.Adventure.Colors.Single(color => color.Color == 1);
        int talentLevel = slots.Where(slot => slot.Color == red.Color && slot.Level > 0
            && slot.UnlockPoint is > 0 && red.Point >= slot.UnlockPoint)
            .Select(slot => slot.Level ?? 0).DefaultIfEmpty(0).Max();
        red.Level = Math.Max(red.Level, talentLevel + 2);
        test.SaveFixture();

        Dictionary<int, (int Level, int Resource, int DailyResource, int Point, int PointCanCost)> before =
            test.Adventure.Colors.ToDictionary(color => color.Color,
                color => (color.Level, color.Resource, color.DailyResource, color.Point, color.PointCanCost));
        int prosperityBefore = test.Adventure.Prosperity;
        Theatre4EffectTable union = TableReaderV2.Parse<Theatre4EffectTable>().Single(row => row.Id == 18);
        int unionMarkup = union.Params.FirstOrDefault();
        double expectedExtra = 1d + (test.Adventure.Items.Count * unionMarkup / 10000d);
        Dictionary<int, int> expectedDelta = before.ToDictionary(pair => pair.Key,
            pair => checked((int)Math.Floor((pair.Value.Level
                * (pair.Value.Resource + pair.Value.DailyResource) * expectedExtra) + 0.5d)));
        int expectedProsperity = checked(prosperityBefore + expectedDelta.Values.Sum());

        JObject daily = test.Call(nameof(Theatre4DailySettleRequest));

        // Keep these first: the old runtime returns zero Point and Prosperity, so the regression
        // stops at the missing development credit before inspecting unrelated wire details.
        foreach ((int color, (int Level, int Resource, int DailyResource, int Point, int PointCanCost) value) in before)
            AssertEqual(expectedDelta[color], test.Adventure.Colors.Single(candidate => candidate.Color == color).Point
                - value.Point, $"Daily settlement colour {color} Point delta");
        AssertEqual(expectedProsperity, test.Adventure.Prosperity,
            "Daily settlement accumulates area Prosperity exactly once");

        JObject settle = RequiredObject(daily, "SettleResult", "Theatre4 development daily settle");
        JObject levels = RequiredObject(settle, "ColorLevel", "Theatre4 development ColorLevel");
        JObject resources = RequiredObject(settle, "ColorResource", "Theatre4 development ColorResource");
        double actualExtra = RequiredToken(settle, "ColorExtra", JTokenType.Float,
            "Theatre4 development ColorExtra").Value<double>();
        AssertEqual(expectedExtra, actualExtra, "Daily settlement returns its fractional ColorExtra");
        foreach ((int color, (int Level, int Resource, int DailyResource, int Point, int PointCanCost) value) in before)
        {
            int expectedLevel = value.Level;
            int expectedResource = value.Resource + value.DailyResource;
            AssertEqual(expectedLevel, RequiredValue<int>(levels, color.ToString(), JTokenType.Integer,
                $"Theatre4 development ColorLevel[{color}]"),
                $"Daily settlement returns ColorLevel[{color}] operand");
            AssertEqual(expectedResource, RequiredValue<int>(resources, color.ToString(), JTokenType.Integer,
                $"Theatre4 development ColorResource[{color}]"),
                $"Daily settlement returns ColorResource[{color}] operand");
            Theatre4ColorTalentData after = test.Adventure.Colors.Single(candidate => candidate.Color == color);
            AssertEqual(value.Resource, after.Resource,
                $"Daily settlement keeps permanent ColorResource[{color}] separate");
            AssertEqual(value.DailyResource, after.DailyResource,
                $"Daily settlement preserves earned ColorDailyResource[{color}]");
            AssertEqual(color == 1 ? expectedDelta[color] : value.PointCanCost, after.PointCanCost,
                $"Daily settlement ColorPointCanCost[{color}] follows local military policy");
        }

        Dictionary<int, NotifyTheatre4ColorResourceData> resourcePushes = test.Pushes
            .Where(push => push.Name == nameof(NotifyTheatre4ColorResourceData))
            .Select(push => MessagePackSerializer.Deserialize<NotifyTheatre4ColorResourceData>(push.Content))
            .GroupBy(data => data.Color)
            .ToDictionary(group => group.Key, group => group.Last());
        AssertEqual(3, resourcePushes.Count, "Daily settlement pushes every colour resource state");
        foreach ((int color, (int Level, int Resource, int DailyResource, int Point, int PointCanCost) value) in before)
        {
            NotifyTheatre4ColorResourceData pushed = resourcePushes[color];
            AssertEqual(test.Adventure.Colors.Single(candidate => candidate.Color == color).Point,
                pushed.Point, $"Colour {color} push carries Point");
            AssertEqual(test.Adventure.Colors.Single(candidate => candidate.Color == color).PointCanCost,
                pushed.PointCanCost, $"Colour {color} push carries PointCanCost");
        }
        Theatre4AdventureData visible = MessagePackSerializer.Deserialize<NotifyTheatre4AdventureData>(
            test.Pushes.Last(push => push.Name == nameof(NotifyTheatre4AdventureData)).Content).AdventureData
            ?? throw new InvalidDataException("Daily settlement unexpectedly removed the active adventure.");
        AssertEqual(1, visible.Days, "Client receives the advanced day");
        AssertEqual(test.Adventure.MaxAp + test.Adventure.ExtraMaxAp, visible.Ap,
            "Client receives restored daily action points");
        AssertEqual(expectedProsperity, visible.Prosperity, "Client receives the earned area score");

        var settledColors = test.Adventure.Colors.OrderBy(color => color.Color)
            .Select(color => (color.Color, color.Level, color.Resource, color.DailyResource,
                color.Point, color.PointCanCost)).ToArray();
        test.Relog("development-after-settle");
        AssertEqual(expectedProsperity, test.Adventure.Prosperity, "Earned area score survives relog");
        AssertEqual(1, test.Adventure.Days, "Settled day survives relog");
        AssertEqual(true, settledColors.SequenceEqual(test.Adventure.Colors.OrderBy(color => color.Color)
            .Select(color => (color.Color, color.Level, color.Resource, color.DailyResource,
                color.Point, color.PointCanCost))), "Earned colour balances and multipliers survive relog");

        // A second day after relog is a fresh registered settlement with no fixture mutation.
        Dictionary<int, (int Level, int Resource, int DailyResource, int Point, int PointCanCost)> secondBefore =
            test.Adventure.Colors.ToDictionary(color => color.Color,
                color => (color.Level, color.Resource, color.DailyResource, color.Point, color.PointCanCost));
        int secondProsperityBefore = test.Adventure.Prosperity;
        Dictionary<int, int> secondDelta = secondBefore.ToDictionary(pair => pair.Key,
            pair => checked((int)Math.Floor((pair.Value.Level
                * (pair.Value.Resource + pair.Value.DailyResource) * expectedExtra) + 0.5d)));
        JObject secondDaily = test.Call(nameof(Theatre4DailySettleRequest));
        foreach ((int color, (int Level, int Resource, int DailyResource, int Point, int PointCanCost) value) in secondBefore)
            AssertEqual(secondDelta[color],
                test.Adventure.Colors.Single(candidate => candidate.Color == color).Point - value.Point,
                $"Relogged daily settlement colour {color} Point delta");
        AssertEqual(secondProsperityBefore + secondDelta.Values.Sum(), test.Adventure.Prosperity,
            "Relogged daily settlement accumulates the second area score once");
        JObject secondSettle = RequiredObject(secondDaily, "SettleResult", "Theatre4 second daily settle");
        AssertEqual(expectedExtra, RequiredToken(secondSettle, "ColorExtra", JTokenType.Float,
            "Theatre4 second ColorExtra").Value<double>(), "Relogged daily returns ColorExtra");
        test.Relog("development-after-second-settle");
        AssertEqual(test.Adventure.Days, 2, "Two daily settlements remain durable across relog");

        using (Theatre4Case zero = new("development-zero"))
        {
            zero.StartRun(1);
            Dictionary<int, int> zeroPoints = zero.Adventure.Colors.ToDictionary(color => color.Color,
                color => color.Point);
            JObject zeroDaily = zero.Call(nameof(Theatre4DailySettleRequest));
            foreach ((int color, int point) in zeroPoints)
                AssertEqual(point, zero.Adventure.Colors.Single(candidate => candidate.Color == color).Point,
                    $"Zero-input day colour {color} Point remains unchanged");
            AssertEqual(0, zero.Adventure.Prosperity, "Zero-input day leaves area Prosperity unchanged");
            JObject zeroSettle = RequiredObject(zeroDaily, "SettleResult", "Theatre4 zero-input settle");
            AssertEqual(1d, zeroSettle.Value<double>("ColorExtra"), "Zero-input day has neutral ColorExtra");
        }

        using (Theatre4Case gate = new("development-gate"))
        {
            gate.StartRun(1);
            gate.Adventure.Ap = 100;
            gate.Adventure.Gold = 100000;
            gate.SaveFixture();
            int gateMap = gate.Adventure.Chapters[0].MapId;
            Theatre4GridData gateGrid = gate.Adventure.Chapters[0].Grids
                .First(grid => grid.Type == T4GridBoss && grid.Fight is { StageId: > 0 });
            gate.ExploreToward(gateMap, gateGrid);
            Theatre4FightGroupTable group = TableReaderV2.Parse<Theatre4FightGroupTable>()
                .First(row => Theatre4RowIndex(row.ProsperityLimit, gate.Adventure.Difficulty) > 0);
            // Generated maps may choose an ungated boss; this fixture uses an authored gated encounter.
            Theatre4FightTable gatedFight = TableReaderV2.Parse<Theatre4FightTable>()
                .Single(row => row.Id == group.FightId);
            Theatre4FightMoldTable gatedMold = TableReaderV2.Parse<Theatre4FightMoldTable>()
                .Single(row => row.Id == gatedFight.MoldId);
            int limit = Theatre4RowIndex(group.ProsperityLimit, gate.Adventure.Difficulty);
            AssertEqual(true, limit > 0, "Area gate uses an authored positive ProsperityLimit");
            Theatre4SeedEffect(gate, 1206); // Authored Type205 enables yellow sweep.
            Theatre4AddAsset(gate, T4AssetColorDailyResource, 1, 1);
            gateGrid = gate.Grid(gateMap, gateGrid.PosX, gateGrid.PosY);
            gateGrid.ContentGroup = group.GroupId;
            gateGrid.ContentId = gatedFight.Id;
            gateGrid.Fight!.FightGroupId = group.Id;
            gateGrid.Fight.StageId = gatedMold.StageId.First(stage => stage > 0);
            gateGrid.Fight.PunishCountdown = Theatre4RowIndex(group.PunishTerm, gate.Adventure.Difficulty);
            Dictionary<int, (int Level, int Resource, int DailyResource)> gateBefore =
                gate.Adventure.Colors.ToDictionary(color => color.Color,
                    color => (color.Level, color.Resource, color.DailyResource));
            int gateGain = gateBefore.Values.Sum(value =>
                checked((int)Math.Floor(value.Level
                    * (value.Resource + value.DailyResource) + 0.5d)));
            gate.Adventure.Prosperity = checked(limit - gateGain);
            gate.SaveFixture();
            gate.Reject(nameof(Theatre4SweepMosnterRequest), new Theatre4SweepMosnterRequest
            {
                MapId = gateMap, PosX = gateGrid.PosX, PosY = gateGrid.PosY, SweepType = 2
            }, "Area gate rejects below authored ProsperityLimit");
            gate.Call(nameof(Theatre4DailySettleRequest));
            AssertEqual(limit, gate.Adventure.Prosperity,
                "Daily development points reach the authored area gate exactly");
            gate.Call(nameof(Theatre4SweepMosnterRequest), new Theatre4SweepMosnterRequest
            {
                MapId = gateMap, PosX = gateGrid.PosX, PosY = gateGrid.PosY, SweepType = 2
            });
        }
    }

    //endregion

    private static Theatre4GridData Theatre4ContinuationFightTarget(Theatre4Case test, int mapId,
        int skipGridId = 0)
    {
        HashSet<int> rewardFightIds = TableReaderV2.Parse<Theatre4FightTable>()
            .Where(row => row.RewardDropId is > 0).Select(row => row.Id).ToHashSet();
        for (int attempt = 0; attempt < 64; attempt++)
        {

            Theatre4ChapterData chapter = test.Chapter(mapId);
            Theatre4GridData? target = chapter.Grids
                .Where(grid => grid.GridId != skipGridId && grid.State == T4StateDiscover
                    && grid.Type != T4GridBoss && grid.Fight is { StageId: > 0 }
                    && rewardFightIds.Contains(grid.ContentId))
                .OrderBy(grid => grid.PosX + grid.PosY).ThenBy(grid => grid.PosX).ThenBy(grid => grid.PosY)
                .FirstOrDefault();
            if (target is not null)
            {
                test.ExploreStep(mapId, target.PosX, target.PosY);
                return test.Grid(mapId, target.PosX, target.PosY);
            }

            Theatre4GridData? next = chapter.Grids
                .Where(grid => grid.State == T4StateDiscover && grid.Type != T4GridBoss)
                .OrderBy(grid => grid.PosX + grid.PosY).ThenBy(grid => grid.PosX).ThenBy(grid => grid.PosY)
                .FirstOrDefault();
            if (next is null)
                throw new InvalidDataException($"No authored continuation fight became discoverable on map {mapId}.");
            test.ExploreStep(mapId, next.PosX, next.PosY);
        }
        throw new InvalidDataException($"Continuation fight target did not become discoverable on map {mapId}.");
    }
    private static FightSettleRequest Theatre4LastHpSettleRequest(Theatre4Case test, out int endingsBefore)
    {
        test.StartRun(1);
        int mapId = test.Adventure.Chapters[0].MapId;
        Theatre4GridData target = Theatre4ContinuationFightTarget(test, mapId);
        test.Adventure.Hp = 1;
        test.SaveFixture();
        Theatre4TeamData team = test.RecruitTeam(2);
        test.Call(nameof(Theatre4FightLocateRequest), new Theatre4FightLocateRequest
        {
            Type = 1, MapId = mapId, PosX = target.PosX, PosY = target.PosY
        });
        test.Call(nameof(Theatre4SetTeamDataRequest), new Theatre4SetTeamDataRequest { TeamData = team });
        test.AuthorizeFrozenFight();
        endingsBefore = test.Data.Endings.Values.Sum();
        return new FightSettleRequest
        {
            Result = Theatre4Case.SettleReport(test.State.ActiveEncounter
                ?? throw new InvalidDataException("Last-HP fixture did not freeze an encounter."), -41L, false)
        };
    }

    //endregion

    //region shared helpers

    private static List<Theatre4DifficultyTable> Theatre4DifficultyRows() =>
        TableReaderV2.Parse<Theatre4DifficultyTable>();

    private static List<Theatre4EndingTable> Theatre4EndingRows() =>
        TableReaderV2.Parse<Theatre4EndingTable>();

    // Client-consumable ending: XTheatre4Agency:235-236 records data.AdventureData through the same
    // XTheatre4Adventure reader as a live snapshot, so the terminal run must ride in that key while
    // SettleData supplies the ending id/type.
    private static void AssertEndingShape(NotifyTheatre4AdventureSettle ending, int settleType, int difficulty,
        string name)
    {
        AssertEqual(true, ending.AdventureData is not null, $"{name}: carries the terminal run snapshot");
        AssertEqual(difficulty, ending.AdventureData!.Difficulty, $"{name}: snapshot is the settled run");
        AssertEqual(true, ending.SettleData is not null, $"{name}: carries the settle summary");
        AssertEqual(settleType, ending.SettleData!.SettleType, $"{name}: settle type");
        AssertEqual(true, Theatre4EndingRows().Any(row => row.Id == ending.SettleData.EndingId
            && row.PassType == (settleType == 2 ? 2 : 1)), $"{name}: ending matches its authored pass type");
        AssertEqual(ending.SettleData.StarBpExp, ending.AdventureData.SettleBpExp,
            $"{name}: terminal snapshot and settle summary carry the same BP reward");
    }

    private static int Theatre4RowIndex(List<int> values, int oneBasedId) => values.Count == 0
        ? 0 : values[Math.Clamp(oneBasedId - 1, 0, values.Count - 1)];

    // Recursive scans over the real PreFight response: EventIds are arrays, MagicIds maps.
    private static bool Theatre4JsonListContains(JToken root, string property, int value)
    {
        if (root is JObject obj)
        {
            foreach (JProperty member in obj.Properties())
            {
                if (member.Name == property && member.Value is JArray list
                    && list.Any(token => token.Type == JTokenType.Integer && token.Value<int>() == value))
                    return true;
                if (Theatre4JsonListContains(member.Value, property, value)) return true;
            }
        }
        else if (root is JArray group)
        {
            foreach (JToken token in group)
                if (Theatre4JsonListContains(token, property, value)) return true;
        }
        return false;
    }

    private static bool Theatre4JsonMapContainsKey(JToken root, string property, int value)
    {
        if (root is JObject obj)
        {
            foreach (JProperty member in obj.Properties())
            {
                if (member.Name == property && member.Value is JObject map
                    && map.Properties().Any(key => int.TryParse(key.Name, out int parsed) && parsed == value))
                    return true;
                if (Theatre4JsonMapContainsKey(member.Value, property, value)) return true;
            }
        }
        else if (root is JArray group)
        {
            foreach (JToken token in group)
                if (Theatre4JsonMapContainsKey(token, property, value)) return true;
        }
        return false;
    }

    private static HashSet<int> Theatre4DeterministicDrops() => TableReaderV2.Parse<Theatre4RewardDropTable>()
        .Where(row => row.GroupIds.Count > 0 && row.Probabilitys.Count >= row.GroupIds.Count
            && Enumerable.Range(0, row.GroupIds.Count).All(index => row.Probabilitys[index] >= 1))
        .Select(row => row.Id).ToHashSet();

    // A real Type-4 grid event whose authored fight row has no reward drop, with the fight's
    // mold stage resolved the same way BuildMapEvent resolves it. IDs/stage come from the tables.
    private static (Theatre4EventTable Event, Theatre4FightTable Fight, int Stage) Theatre4RewardlessEventFight()
    {
        Dictionary<int, Theatre4FightTable> fights = TableReaderV2.Parse<Theatre4FightTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, Theatre4FightMoldTable> molds = TableReaderV2.Parse<Theatre4FightMoldTable>()
            .ToDictionary(row => row.Id);
        foreach (Theatre4EventTable evt in TableReaderV2.Parse<Theatre4EventTable>())
        {
            if (evt.Type != 4 || !(evt.FightId is > 0)) continue;
            if (!fights.TryGetValue(evt.FightId.Value, out Theatre4FightTable? fight) || fight.RewardDropId is > 0)
                continue;
            if (!molds.TryGetValue(fight.MoldId, out Theatre4FightMoldTable? mold)) continue;
            int stage = mold.StageId.FirstOrDefault(id => id > 0);
            if (stage > 0) return (evt, fight, stage);
        }
        throw new InvalidDataException("No authored reward-less Type-4 event fight exists.");
    }

    private static (Theatre4EventTable Event, Theatre4FightTable Fight, int Stage) Theatre4UntimedEventFight()
    {
        Dictionary<int, Theatre4FightTable> fights = TableReaderV2.Parse<Theatre4FightTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, Theatre4FightMoldTable> molds = TableReaderV2.Parse<Theatre4FightMoldTable>()
            .ToDictionary(row => row.Id);
        HashSet<int> untimed = TableReaderV2.Parse<StageTable>()
            .Where(row => row.PassTimeLimit is not > 0).Select(row => row.StageId).ToHashSet();
        foreach (Theatre4EventTable evt in TableReaderV2.Parse<Theatre4EventTable>())
        {
            if (evt.Type != 4 || evt.FightId is not > 0
                || !fights.TryGetValue(evt.FightId.Value, out Theatre4FightTable? fight)
                || !molds.TryGetValue(fight.MoldId, out Theatre4FightMoldTable? mold))
                continue;
            int stage = mold.StageId.FirstOrDefault(untimed.Contains);
            if (stage > 0) return (evt, fight, stage);
        }
        throw new InvalidDataException("No authored untimed Theatre4 event fight exists.");
    }

    private static void Theatre4AssertItemRewards(Theatre4Case test, IReadOnlyDictionary<int, long> before,
        IEnumerable<int> rewardIds, string name)
    {
        HashSet<int> itemTemplates = TableReaderV2.Parse<ItemTable>().Select(row => row.Id).ToHashSet();
        List<RewardGoodsTable> goods = rewardIds.Where(id => id > 0)
            .SelectMany(id => ResolveRewardGoods(id, TableReaderV2.Parse<RewardGoodsTable>(), name)).ToList();
        foreach (IGrouping<int, RewardGoodsTable> group in goods
            .Where(row => itemTemplates.Contains(row.TemplateId)).GroupBy(row => row.TemplateId))
            AssertEqual(before.GetValueOrDefault(group.Key) + group.Sum(row => (long)row.Count), test.Balance(group.Key),
                $"{name} item {group.Key} is additive exactly once");
    }

    private static void Theatre4SeedItem(Theatre4Case test, int itemId) =>
        Theatre4AddAsset(test, T4AssetItem, itemId, 1);

    private static void Theatre4SeedEffect(Theatre4Case test, int effectId)
    {
        Theatre4AdventureData adventure = test.Adventure;
        if (adventure.CustomEffects.Any(effect => effect.EffectId == effectId)) return;
        adventure.CustomEffects.Add(new Theatre4EffectData
        {
            Id = adventure.IdSequence + 1, EffectId = effectId
        });
        adventure.IdSequence += 1;
        test.SaveFixture();
    }

    private static void Theatre4GrantDrop(Theatre4Case test, int dropId) =>
        Theatre4InvokeAsset(test, "GrantModeDrop", dropId);

    private static void Theatre4AddAsset(Theatre4Case test, int type, int id, int count) =>
        Theatre4InvokeAsset(test, "AddAsset", type, id, count);

    // Runs an internal asset helper on a throwaway Mutation and adopts the resulting state, so a
    // fixture goes through the same clamp/notification/offer code path as gameplay.
    private static void Theatre4InvokeAsset(Theatre4Case test, string method, params int[] args)
    {
        object mutation = Theatre4NewMutation(test);
        Type mutationType = mutation.GetType();
        Type module = mutationType.DeclaringType!;
        Type[] parameters = [mutationType, .. args.Select(_ => typeof(int))];
        RequiredMethod(module, method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, parameters)
            .Invoke(null, [mutation, .. args.Cast<object>()]);
        test.AdoptMutationState(mutation);
    }

    private static Theatre4TransactionData? Theatre4CreateFightReward(Theatre4Case test, int fightId)
    {
        object mutation = Theatre4NewMutation(test);
        Type mutationType = mutation.GetType();
        Type module = mutationType.DeclaringType!;
        object? created = RequiredMethod(module, "CreateFightRewardTransaction",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [mutationType, typeof(int)])
            .Invoke(null, [mutation, fightId]);
        test.AdoptMutationState(mutation);
        return created as Theatre4TransactionData;
    }

    private static object Theatre4NewMutation(Theatre4Case test)
    {
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre4Module");
        Type mutationType = module.GetNestedType("Mutation", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(module.FullName, "Mutation");
        return Activator.CreateInstance(mutationType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null, args: [test.Session, false], culture: null)!;
    }

    private static Theatre4GridData Theatre4GridOfType(Theatre4Case test, int type) =>
        test.Adventure.Chapters[0].Grids.Where(grid => grid.Type == type)
            .OrderByDescending(grid => grid.PosX + grid.PosY).FirstOrDefault()
        ?? throw new InvalidDataException($"Chapter has no grid of type {type}.");

    // Re-authors a distinct spare tile as a completed Empty tile, so a later skill fixture still
    // has a valid target after earlier builds consumed the authored Empty tiles.
    private static Theatre4GridData Theatre4EmptyTile(Theatre4Case test, int mapId)
    {
        Theatre4GridData host = test.Adventure.Chapters[0].Grids
            .Where(grid => grid.Type is not (T4GridBoss or T4GridStart or T4GridBuilding or T4GridEvent))
            .OrderByDescending(grid => grid.PosX + grid.PosY)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Theatre4 map has no spare tile for a build fixture.");
        Theatre4GridData live = test.Grid(mapId, host.PosX, host.PosY);
        live.Type = T4GridEmpty;
        live.State = T4StateProcessed;
        live.Fight = null;
        live.Shop = null;
        live.Event = null;
        live.Building = null;
        test.SaveFixture();
        return live;
    }

    private sealed class Theatre4Case : IDisposable
    {
        private static long nextPlayerId = 97_100;
        private int packetId;
        private readonly MongoCollectionOverride collections;
        public readonly RecordingMongoCollectionProxy<Player> Players;
        public readonly RecordingMongoCollectionProxy<Character> Characters;
        public readonly RecordingMongoCollectionProxy<Inventory> Inventories;
        public readonly RecordingMongoCollectionProxy<Stage> Stages;
        public LoopbackSessionHarness Harness { get; private set; }
        public Session Session => Harness.Session;
        public PlayerTheatre4State State => Session.player.Theatre4;
        public Theatre4ActivityData Data => State.Data;
        public Theatre4AdventureData Adventure => State.Data.AdventureData
            ?? throw new InvalidDataException("Theatre4 adventure is not active.");
        public List<Packet.Push> Pushes { get; } = [];
        public byte[] LastResponseContent { get; private set; } = [];
        public string LastRequestName { get; private set; } = "";

        public Theatre4Case(string name)
        {
            collections = MongoCollectionOverride.InstallForBiancaCompatibility(
                out Players, out Characters, out Inventories, out Stages);
            long id = ++nextPlayerId;
            Harness = new(CreateDrawCompatibilityCharacter(id), CreateDrawCompatibilityPlayer(id),
                CreateDrawCompatibilityInventory(id, []), $"theatre4-{name}");
            Session.stage = CreateLoginAccountCompatibilityStage(id);
            // Real feature gate 1020681 is Commandant level 80; no bypass.
            Session.player.PlayerData.Level = 80;
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre4Module"), "PrepareLogin",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Session)]).Invoke(null, [Session]);
            Login();
            SaveFixture();
        }

        public NotifyTheatre4ActivityData Login() => (NotifyTheatre4ActivityData)RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre4Module"), "BuildLoginData",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Session)]).Invoke(null, [Session])!;

        public void AdoptMutationState(object mutation)
        {
            Session.player.Theatre4 = (PlayerTheatre4State)mutation.GetType()
                .GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(mutation)!;
            SaveFixture();
        }

        public int NextPacketId => packetId + 1;

        public JObject Call(string requestName, object? request = null, bool? success = true,
            bool verifyPersistence = true, int? transportId = null)
        {
            Pushes.Clear();
            int id = transportId ?? ++packetId;
            Theatre4Calls.Add(requestName);
            LastRequestName = requestName;
            InvokeRegisteredRequestHandler(requestName, Session, id, request);
            for (int index = 0; index < 512; index++)
            {
                Packet packet = Harness.ReadPacket($"{requestName} result {index}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, $"{requestName} response packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, $"{requestName} correlation");
                AssertEqual(requestName.Replace("Request", "Response", StringComparison.Ordinal), response.Name,
                    $"{requestName} response name");
                LastResponseContent = response.Content;
                JObject body = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                int code = RequiredValue<int>(body, "Code", JTokenType.Integer, requestName);
                if (success.HasValue)
                    AssertEqual(success.Value, code == 0, $"{requestName} accepted={success}, actual code={code}");
                if (Harness.TryReadAvailablePacket($"{requestName} trailing packet", out Packet extra))
                    throw new InvalidDataException($"{requestName}: duplicate response/push after response ({extra.Type}).");
                if (verifyPersistence && code == 0 && requestName.StartsWith("Theatre4", StringComparison.Ordinal))
                {
                    Player saved = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException($"{requestName}: success without a durable player record."));
                    AssertEqual(true, State.ToBson().SequenceEqual(saved.Theatre4.ToBson()),
                        $"{requestName} persists before acknowledgement");
                }
                return body;
            }
            throw new InvalidDataException($"{requestName}: no response within 512 packets.");
        }

        public void StartRun(int difficulty, List<Packet.Push>? difficultyPushes = null)
        {
            Call(nameof(Theatre4SelectDifficultRequest), new Theatre4SelectDifficultRequest { Difficult = difficulty });
            difficultyPushes?.AddRange(Pushes);
            Theatre4AffixTable affix = TableReaderV2.Parse<Theatre4AffixTable>().First(row => !(row.ConditionId > 0));
            Call(nameof(Theatre4SelectAffixRequest), new Theatre4SelectAffixRequest { Affix = affix.Id });
        }

        public void EnsureTechCoin(int count)
        {
            Item? coin = Session.inventory.Items.SingleOrDefault(item => item.Id == 96200);
            if (coin is null) Session.inventory.Items.Add(new Item { Id = 96200, Count = count });
            else coin.Count = count;
            SaveFixture();
        }

        public void SetGold(int gold)
        {
            Adventure.Gold = gold;
            SaveFixture();
        }

        public void SetBuildPoint(int buildPoint)
        {
            Adventure.Bp = buildPoint;
            SaveFixture();
        }

        public void SetItemLimit(int itemLimit)
        {
            Adventure.ItemLimit = itemLimit;
            SaveFixture();
        }

        public void SaveFixture()
        {
            Session.player.SaveChecked();
            Session.character.SaveChecked();
            Session.inventory.SaveChecked();
        }

        public long Balance(int itemId) => Session.inventory.Items
            .Where(item => item.Id == itemId).Sum(item => (long)item.Count);

        public Dictionary<int, long> ItemCounts() => Session.inventory.Items
            .GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Count));

        public int Asset(int type, int id) => type switch
        {
            T4AssetGold => Adventure.Gold,
            T4AssetBuildPoint => Adventure.Bp,
            T4AssetActionPoint => Adventure.Ap,
            T4AssetTimeBack => Adventure.TracebackPoint,
            _ => throw new InvalidDataException($"Theatre4 fixture asset {type} is not exposed."),
        };

        public Theatre4ChapterData Chapter(int mapId) => Adventure.Chapters.First(chapter => chapter.MapId == mapId);

        public Theatre4GridData Grid(int mapId, int x, int y) =>
            Chapter(mapId).Grids.First(grid => grid.PosX == x && grid.PosY == y);

        public Theatre4ShopData Shop(int mapId, int x, int y) =>
            Grid(mapId, x, y).Shop ?? throw new InvalidDataException($"Grid {mapId}/{x}/{y} has no shop.");


        private (byte[] Data, string Items, string Roster, byte[] Encounter) ObservableState() =>
        (
            Data.ToBson(),
            string.Join(";", ItemCounts().OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}:{pair.Value}")),
            string.Join(",", Session.character.Characters.Select(character => character.Id).Order()),
            State.ActiveEncounter?.ToBson() ?? []
        );

        public void Reject(string requestName, object? request, string name)
        {
            (byte[] Data, string Items, string Roster, byte[] Encounter) before = ObservableState();
            Call(requestName, request, success: false);
            (byte[] Data, string Items, string Roster, byte[] Encounter) after = ObservableState();
            AssertEqual(true, before.Data.SequenceEqual(after.Data), $"{name}: rejection preserves mode data");
            AssertEqual(before.Items, after.Items, $"{name}: rejection preserves account balances");
            AssertEqual(before.Roster, after.Roster, $"{name}: rejection preserves the roster");
            AssertEqual(true, before.Encounter.SequenceEqual(after.Encounter),
                $"{name}: rejection preserves the active encounter");
        }

        public void RepeatRejected(string requestName, object? request, string name)
        {
            (byte[] Data, string Items, string Roster, byte[] Encounter) before = ObservableState();
            // A new transport id re-executes the action; a once-only claim must reject (or no-op)
            // while leaving observable mode state and account data untouched.
            Call(requestName, request, success: null);
            (byte[] Data, string Items, string Roster, byte[] Encounter) after = ObservableState();
            AssertEqual(true, before.Data.SequenceEqual(after.Data), $"{name}: repeat cannot advance mode data");
            AssertEqual(before.Items, after.Items, $"{name}: repeat cannot change account balances");
            AssertEqual(before.Roster, after.Roster, $"{name}: repeat cannot change the roster");
            AssertEqual(true, before.Encounter.SequenceEqual(after.Encounter),
                $"{name}: repeat preserves the active encounter");
        }

        public void Relog(string name, bool pending = false)
        {
            Player player = BsonSerializer.Deserialize<Player>(Players.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException($"Relog {name}: no persisted player."));
            Character character = BsonSerializer.Deserialize<Character>(
                Characters.LastSuccessfulReplacementBson ?? Session.character.ToBson());
            Inventory inventory = BsonSerializer.Deserialize<Inventory>(
                Inventories.LastSuccessfulReplacementBson ?? Session.inventory.ToBson());
            Stage stage = BsonSerializer.Deserialize<Stage>(Session.stage.ToBson());
            Harness.Dispose();
            Harness = new(character, player, inventory, $"theatre4-relog-{name}");
            Session.stage = stage;
            RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre4Module"), "PrepareLogin",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Session)]).Invoke(null, [Session]);
            NotifyTheatre4ActivityData login = Login();
            if (!pending)
                AssertEqual(Data.AdventureData is not null, login.Data.AdventureData is not null,
                    $"{name}: login adventure gate matches the stored snapshot");
            AssertEqual(true, login.Data.ActivityId > 0, $"{name}: login keeps the activity");
        }

        public NotifyTheatre4AdventureSettle EndingPush(string name) =>
            MessagePackSerializer.Deserialize<NotifyTheatre4AdventureSettle>(
                Pushes.Single(push => push.Name == nameof(NotifyTheatre4AdventureSettle)).Content);

        public void PublishRecoveredEnding() => RequiredMethod(
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre4Module"), "SendRecoveredSettlement",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Session)]).Invoke(null, [Session]);

        // Real login recovery: fresh client context (no active adventure), then the exact login push
        // the production login sequence sends. Ending and reward state must not drift across it.
        public NotifyTheatre4AdventureSettle RelogEnding(string name)
        {
            int endings = Data.Endings.Values.Sum();
            Dictionary<int, long> items = ItemCounts();
            Relog(name, pending: true);
            PublishRecoveredEnding();
            NotifyTheatre4AdventureSettle recovered = ReadPushPayload<NotifyTheatre4AdventureSettle>(
                Harness, nameof(NotifyTheatre4AdventureSettle), $"{name} login ending recovery");
            AssertEqual(endings, Data.Endings.Values.Sum(), $"{name}: login recovery never re-grants endings");
            AssertEqual(true, items.OrderBy(pair => pair.Key).SequenceEqual(ItemCounts().OrderBy(pair => pair.Key)),
                $"{name}: login recovery never re-grants rewards");
            return recovered;
        }

        //region map driving

        public Theatre4GridData ExploreToward(int mapId, Theatre4GridData target)
        {
            int guard = 0;
            int targetX = target.PosX, targetY = target.PosY;
            while (guard++ < 64)
            {
                Theatre4GridData live = Grid(mapId, targetX, targetY);
                if (live.State is T4StateExplored or T4StateProcessed) return live;
                if (live.State == T4StateDiscover)
                {
                    ExploreStep(mapId, targetX, targetY);
                    return Grid(mapId, targetX, targetY);
                }
                List<Theatre4GridData> discover = Chapter(mapId).Grids
                    .Where(grid => grid.State == T4StateDiscover).ToList();
                if (discover.Count == 0)
                    throw new InvalidDataException($"No discovered tile can reach {mapId}/{targetX}/{targetY}.");
                Theatre4GridData next = discover
                    .OrderBy(grid => Math.Abs(grid.PosX - targetX) + Math.Abs(grid.PosY - targetY)).First();
                ExploreStep(mapId, next.PosX, next.PosY);
            }
            throw new InvalidDataException($"Tile {mapId}/{targetX}/{targetY} was not reachable.");
        }

        public void ExploreUntilDiscover(int mapId, Theatre4GridData target)
        {
            int guard = 0;
            int targetX = target.PosX, targetY = target.PosY;
            while (guard++ < 64)
            {
                int state = Grid(mapId, targetX, targetY).State;
                if (state is T4StateDiscover or T4StateExplored or T4StateProcessed) return;
                List<Theatre4GridData> discover = Chapter(mapId).Grids
                    .Where(grid => grid.State == T4StateDiscover).ToList();
                if (discover.Count == 0)
                    throw new InvalidDataException($"No discovered tile can reach {mapId}/{targetX}/{targetY}.");
                Theatre4GridData next = discover
                    .OrderBy(grid => Math.Abs(grid.PosX - targetX) + Math.Abs(grid.PosY - targetY)).First();
                ExploreStep(mapId, next.PosX, next.PosY);
            }
            throw new InvalidDataException($"Tile {mapId}/{targetX}/{targetY} never became discoverable.");
        }

        public void ExploreStep(int mapId, int x, int y)
        {
            if (Adventure.Ap < 1)
            {
                Call(nameof(Theatre4DailySettleRequest));
                if (Data.AdventureData is null)
                    throw new InvalidDataException("Run ended before the target tile was reached.");
            }
            Call(nameof(Theatre4ExploreGridRequest), new Theatre4ExploreGridRequest { MapId = mapId, PosX = x, PosY = y });
        }

        public Theatre4GridData ExploreUntilFight(int mapId)
        {
            int guard = 0;
            while (guard++ < 64)
            {
                Theatre4GridData? candidate = Chapter(mapId).Grids
                    .Where(grid => grid.State == T4StateDiscover && grid.Fight is not null)
                    .OrderBy(grid => grid.PosX + grid.PosY).FirstOrDefault();
                if (candidate is not null)
                {
                    ExploreStep(mapId, candidate.PosX, candidate.PosY);
                    return Grid(mapId, candidate.PosX, candidate.PosY);
                }
                List<Theatre4GridData> discover = Chapter(mapId).Grids
                    .Where(grid => grid.State == T4StateDiscover).ToList();
                if (discover.Count == 0) throw new InvalidDataException("Map exhausted without a reachable fight.");
                Theatre4GridData next = discover.OrderBy(grid => grid.PosX + grid.PosY).First();
                ExploreStep(mapId, next.PosX, next.PosY);
            }
            throw new InvalidDataException("No fight tile became reachable.");
        }

        //endregion

        //region combat driving

        public Theatre4TeamData RecruitTeam(int count = 1)
        {
            Theatre4TransactionData? recruit = Adventure.Transactions.FirstOrDefault(transaction => transaction.Type == 1);
            if (recruit is not null)
            {
                Call(nameof(Theatre4ConfirmRecruitRequest),
                    new Theatre4ConfirmRecruitRequest { TransactionId = recruit.Id, Index = recruit.Characters.Count });
            }
            while (Adventure.Characters.Count < count)
            {
                HashSet<int> recruited = Adventure.Characters.Select(character => character.CharacterId).ToHashSet();
                Theatre4CharacterTable row = TableReaderV2.Parse<Theatre4CharacterTable>()
                    .Where(candidate => candidate.RobotId > 0 && !recruited.Contains(candidate.Id))
                    .First(candidate => TableReaderV2.Parse<RobotTable>().Any(robot => robot.Id == candidate.RobotId));
                // Explicit roster fixture: recruitment itself is covered by the economy scenario.
                Adventure.Characters.Add(new Theatre4CharacterData { CharacterId = row.Id, Star = 1 });
                SaveFixture();
            }
            List<int> robots = Adventure.Characters.Take(count)
                .Select(recruited => TableReaderV2.Parse<Theatre4CharacterTable>()
                    .Single(candidate => candidate.Id == recruited.CharacterId).RobotId).ToList();
            return new Theatre4TeamData
            {
                CardIds = [0, 0, 0],
                RobotIds = [.. robots, .. Enumerable.Repeat(0, 3 - robots.Count)],
                CaptainPos = 1,
                FirstFightPos = 1
            };
        }

        public void RecruitAndSetTeam()
        {
            if (Adventure.TeamData is not null) return;
            Call(nameof(Theatre4SetTeamDataRequest),
                new Theatre4SetTeamDataRequest { TeamData = RecruitTeam() });
            AssertEqual(true, Adventure.TeamData is not null, "Authored robot roster is accepted as a team");
        }

        public JObject AuthorizeFrozenFight()
        {
            Theatre4ActiveEncounter encounter = State.ActiveEncounter
                ?? throw new InvalidDataException("No frozen encounter to authorize.");
            Theatre4TeamData team = Adventure.TeamData
                ?? throw new InvalidDataException("Team is required before a native fight.");
            PreFightRequest preFight = new()
            {
                PreFightData = new PreFightRequest.PreFightRequestPreFightData
                {
                    ChallengeCount = 1,
                    StageId = (uint)encounter.StageId,
                    CardIds = team.CardIds.Select(id => (uint)id).ToList(),
                    RobotIds = [.. team.RobotIds],
                    CaptainPos = team.CaptainPos,
                    FirstFightPos = team.FirstFightPos,
                    EnterCgIndex = team.EnterCgIndex,
                    SettleCgIndex = team.SettleCgIndex,
                    GeneralSkill = 0
                }
            };
            JObject prepared = Call(nameof(PreFightRequest), preFight, verifyPersistence: false);
            AssertEqual(0, prepared.Value<int>("Code"), "Native PreFight authorizes the frozen stage");
            return prepared;
        }

        public JObject? ResolveFrozenFight(long leftTime = 0, bool win = true)
        {
            Theatre4ActiveEncounter encounter = State.ActiveEncounter
                ?? throw new InvalidDataException("No frozen encounter to resolve.");
            FightSettleResult report = SettleReport(encounter, leftTime, win);
            if (encounter.SettleReceipt is not null)
            {
                JObject replay = Call(nameof(FightSettleRequest), new FightSettleRequest { Result = report }, verifyPersistence: false);
                AssertEqual(0, replay.Value<int>("Code"), "Duplicate settle replays the frozen receipt");
                return null;
            }
            JObject prepared = AuthorizeFrozenFight();
            JObject settled = Call(nameof(FightSettleRequest), new FightSettleRequest { Result = report }, verifyPersistence: false);
            AssertEqual(0, settled.Value<int>("Code"), "Native settle wins the frozen stage");
            return prepared;
        }

        public static FightSettleResult SettleReport(Theatre4ActiveEncounter encounter, long leftTime = 0, bool win = true) => new()
        {
            IsWin = win,
            StageId = (uint)encounter.StageId,
            FightId = encounter.FightUuid,
            RebootCount = 0,
            StartFrame = 0,
            SettleFrame = 20,
            PauseFrame = 0,
            ExSkillPauseFrame = 0,
            LeftTime = leftTime,
            IsForceExit = false,
            DamageSourceDic = [],
            StringToListIntRecord = [],
            StringToIntRecord = []
        };

        //endregion

        //region complete run

        public bool CompleteRun()
        {
            int guard = 0;
            while (Data.AdventureData is not null && guard++ < 2048)
            {
                Theatre4ChapterData chapter = Data.AdventureData.Chapters[^1];
                Theatre4GridData boss = chapter.Grids.FirstOrDefault(grid => grid.Type == T4GridBoss)
                    ?? throw new InvalidDataException("Chapter has no authored boss tile.");
                if (boss.State is T4StateExplored or T4StateProcessed)
                {
                    RecruitAndSetTeam();
                    Call(nameof(Theatre4FightLocateRequest),
                        new Theatre4FightLocateRequest { Type = 1, MapId = chapter.MapId, PosX = boss.PosX, PosY = boss.PosY });
                    ResolveFrozenFight();
                    continue;
                }
                ExploreToward(chapter.MapId, boss);
            }
            return Data.AdventureData is null && Data.Difficultys.GetValueOrDefault(1) >= 1;
        }

        //endregion

        public void Dispose()
        {
            Harness.Dispose();
            collections.Dispose();
        }
    }

    //endregion
}
