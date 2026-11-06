using System.Reflection;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.punishaar;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    // Circuit Calculus = Punishaar (installed 4.8 client Modules/Xpunishaar, activity TimeId 50201).
    // Covers the 19 requests with their exact wire field names, the table-driven shop/node policy,
    // placement/upgrade/buyback rules, the durable run lifecycle (start -> every authored node type ->
    // settlement) and the persistence edges the client depends on: a failed write is settled against the
    // durable document (adopted when it landed, discarded when it did not, session closed when the
    // document cannot be read), and a transport retry of the newest packet id never applies the action
    // twice while a retired id is refused outright.
    public static void RunPunishaarCompatibility()
    {
        PunishaarWireContract();
        PunishaarShopPolicyChecks();
        PunishaarRunMechanicsChecks();
        PunishaarSessionLifecycle();
    }

    private static readonly Type PunishaarModuleType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PunishaarModule");

    private static MethodInfo PunishaarMethod(string name, params Type[] parameterTypes) =>
        RequiredMethod(PunishaarModuleType, name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, parameterTypes);

    private static void PunishaarExpectCode(int expectedCode, Action action, string name)
    {
        try
        {
            action();
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is ServerCodeException inner)
        {
            AssertEqual(expectedCode, inner.Code, name);
            return;
        }
        throw new InvalidDataException($"{name}: expected Punishaar Code {expectedCode}, nothing was rejected.");
    }

    // The wire names are the client contract: the installed 4.8 Lua reads responses and notifies by these
    // exact keys, and the server-only run bookkeeping must never leak into them.
    private static void PunishaarWireContract()
    {
        AssertMailNamedMapKeys(new XPunishaarGetDataResponse(), ["Code", "DataDb"], "XPunishaarGetDataResponse");
        AssertMailNamedMapKeys(new XPunishaarStartStageResponse(), ["Code", "Stage"], "XPunishaarStartStageResponse");
        AssertMailNamedMapKeys(new XPunishaarEnterStageResponse(), ["Code", "Stage"], "XPunishaarEnterStageResponse");
        AssertMailNamedMapKeys(new XPunishaarAwayStageResponse(), ["Code"], "XPunishaarAwayStageResponse");
        AssertMailNamedMapKeys(new XPunishaarQuitStageResponse(), ["Code", "SettleInfo"], "XPunishaarQuitStageResponse");
        AssertMailNamedMapKeys(new XPunishaarSelectShopResponse(), ["Code", "Node"], "XPunishaarSelectShopResponse");
        AssertMailNamedMapKeys(new XPunishaarFinishFightResponse(), ["Code", "Stage", "SettleInfo"], "XPunishaarFinishFightResponse");
        AssertMailNamedMapKeys(new XPunishaarExitNodeResponse(), ["Code", "Stage", "SettleInfo"], "XPunishaarExitNodeResponse");
        AssertMailNamedMapKeys(new XPunishaarDiscardCardResponse(), ["Code"], "XPunishaarDiscardCardResponse");
        AssertMailNamedMapKeys(new XPunishaarSetCardPosResponse(), ["Code"], "XPunishaarSetCardPosResponse");
        AssertMailNamedMapKeys(new XPunishaarHandlePendingRewardResponse(), ["Code", "Node"], "XPunishaarHandlePendingRewardResponse");

        AssertMailNamedMapKeys(new XPunishaarStartStageRequest { StageId = 100101 }, ["StageId"], "XPunishaarStartStageRequest");
        AssertMailNamedMapKeys(new XPunishaarEnterStageRequest { StageId = 100101 }, ["StageId"], "XPunishaarEnterStageRequest");
        AssertMailNamedMapKeys(new XPunishaarAwayStageRequest { StageId = 100101 }, ["StageId"], "XPunishaarAwayStageRequest");
        AssertMailNamedMapKeys(new XPunishaarSelectShopRequest { ShopId = 5 }, ["ShopId"], "XPunishaarSelectShopRequest");
        AssertMailNamedMapKeys(new XPunishaarBuyGoodsRequest(), ["CardDetail", "Index"], "XPunishaarBuyGoodsRequest");
        AssertMailNamedMapKeys(new XPunishaarFreezeGoodsRequest(), ["Index", "IsFreeze"], "XPunishaarFreezeGoodsRequest");
        AssertMailNamedMapKeys(new XPunishaarSellCardRequest { MasterCardId = 1 }, ["MasterCardId"], "XPunishaarSellCardRequest");
        AssertMailNamedMapKeys(new XPunishaarDiscardCardRequest(), ["IsMasterCard", "MasterCardId"], "XPunishaarDiscardCardRequest");
        AssertMailNamedMapKeys(new XPunishaarSetCardPosRequest(), ["CardPosList"], "XPunishaarSetCardPosRequest");
        AssertMailNamedMapKeys(new XPunishaarSelectFightRequest { FightId = 1 }, ["FightId"], "XPunishaarSelectFightRequest");
        AssertMailNamedMapKeys(new XPunishaarSelectEventRequest { EventId = 1 }, ["EventId"], "XPunishaarSelectEventRequest");
        AssertMailNamedMapKeys(new XPunishaarHandlePendingRewardRequest(), ["CardDetail", "IsAccept"], "XPunishaarHandlePendingRewardRequest");
        AssertMailNamedMapKeys(new XPunishaarFinishFightRequest(),
            ["AutoUseSkillCount", "BallConsumption", "BallProduction", "EndHp", "EnemyEndHp", "EnemyStartHp",
                "FightSpeed", "FightTime", "IsAutoFight", "IsWin", "LoseMaxSignalBallColor", "StartHp", "UseSkillCount"],
            "XPunishaarFinishFightRequest");

        AssertMailNamedMapKeys(new PunishaarRewardCardDetailInfo(),
            ["AreaType", "CardPosList", "IsCardsPosChange", "MasterCardId", "StartPos", "SubCardId"], "PunishaarRewardCardDetailInfo");
        AssertMailNamedMapKeys(new PunishaarCardPosInfo { Id = 1, AreaType = 1, StartPos = 1 },
            ["AreaType", "Id", "StartPos"], "PunishaarCardPosInfo");
        AssertMailNamedMapKeys(new PunishaarGoods(),
            ["CardId", "Frozen", "IsBought", "Level", "SubCardId"], "PunishaarGoods");
        AssertMailNamedMapKeys(new PunishaarShopInfo(),
            ["CandidateShopIds", "Goods", "RefreshTimes", "SelectedShopId"], "PunishaarShopInfo");
        AssertMailNamedMapKeys(new PunishaarEventInfo { RandomEventIds = [1], SelectedEventId = 1 },
            ["RandomEventIds", "SelectedEventId"], "PunishaarEventInfo");
        AssertMailNamedMapKeys(new PunishaarFightInfo { RandomFightIds = [1], SelectedFightId = 1 },
            ["RandomFightIds", "SelectedFightId"], "PunishaarFightInfo");
        AssertMailNamedMapKeys(new PunishaarNode(),
            ["EventInfo", "FightInfo", "NodeId", "PendingRewardCardId", "PendingRewardCardLevel", "ShopInfo", "Status", "Type"],
            "PunishaarNode");
        // Server-only run bookkeeping (AllGold/RngState/NextCardId/WonNodeCounts) must never reach the wire.
        AssertMailNamedMapKeys(new PunishaarStage(),
            ["BagGridLimit", "CurrentNode", "CurrentRound", "Durability", "FightAreaGridLimit", "FightWinCount",
                "Gold", "HistoryNodeList", "StageId", "TotalMasterCards"], "PunishaarStage");
        AssertMailNamedMapKeys(new PunishaarMasterCard(),
            ["AreaType", "Id", "Level", "StartPos", "SubCardId", "TemplateId"], "PunishaarMasterCard");
        AssertMailNamedMapKeys(new PunishaarSettleInfo(),
            ["AllGold", "CurrentRound", "Durability", "FightWinCount", "IsNewRecord", "SettleType", "StageId"], "PunishaarSettleInfo");
        AssertMailNamedMapKeys(new PunishaarDataDb(),
            ["ActivityId", "CharacterCardCatalogsDict", "CurrentStageId", "EquipCatalogs", "PartnerCardCatalogsDict",
                "PassedStageIds", "ResonanceCatalogs", "StageChallengeCounts", "StageSaves"], "PunishaarDataDb");
        AssertMailNamedMapKeys(new PunishaarRewardGoods(),
            ["Amount", "CardId", "Level", "RewardType"], "PunishaarRewardGoods");

        AssertMailNamedMapKeys(new NotifyPunishaarLoginData(),
            ["ActivityId", "CharacterCardCatalogsDict", "EquipCatalogs", "PartnerCardCatalogsDict", "PassStageIds",
                "ResonanceCatalogs", "SaveStageIds"], "NotifyPunishaarLoginData");
        AssertMailNamedMapKeys(new NotifyPunishaarGoldChange(), ["Gold"], "NotifyPunishaarGoldChange");
        AssertMailNamedMapKeys(new NotifyPunishaarMasterCardChange(), ["AddedCard", "RemovedCardIds"], "NotifyPunishaarMasterCardChange");
        AssertMailNamedMapKeys(new NotifyPunishaarSubCardChange(), ["MasterCardId", "SubCardId"], "NotifyPunishaarSubCardChange");
        AssertMailNamedMapKeys(new NotifyPunishaarRewardResult(), ["RewardGoodsList", "StageId"], "NotifyPunishaarRewardResult");
    }

    private static object PunishaarShopPolicy() => PunishaarMethod("GetShopPolicy").Invoke(null, null)!;

    private static TValue PunishaarPolicyProperty<TValue>(string name)
    {
        object policy = PunishaarShopPolicy();
        return (TValue)(policy.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidDataException($"Punishaar shop policy has no {name}.")).GetValue(policy)!;
    }

    private static void PunishaarShopPolicyChecks()
    {
        Dictionary<int, List<int>> pools = PunishaarPolicyProperty<Dictionary<int, List<int>>>("PoolByShopGroup");
        Dictionary<int, int> remedySupplies = PunishaarPolicyProperty<Dictionary<int, int>>("SupplyByRemedyGroup");
        Dictionary<int, List<int>> stock = PunishaarPolicyProperty<Dictionary<int, List<int>>>("StockByShop");

        Dictionary<int, PunishaarCardTable> cards = TableReaderV2.Parse<PunishaarCardTable>().ToDictionary(row => row.Id);
        HashSet<(int CardId, int Level)> levels = TableReaderV2.Parse<PunishaarCardLevelTable>().Select(row => (row.CardId, row.Level)).ToHashSet();
        Dictionary<int, PunishaarShopTable> shops = TableReaderV2.Parse<PunishaarShopTable>().ToDictionary(row => row.Id);
        PunishaarStageGroupTable[] stages = TableReaderV2.Parse<PunishaarStageGroupTable>().OrderBy(row => row.StageId).ToArray();

        List<int> supplyShops = shops.Values.Where(row => row.Name.StartsWith("Supply", StringComparison.Ordinal)).Select(row => row.Id).ToList();
        List<int> remedyGroups = stages.Select(row => row.RemedyShop).Distinct().OrderBy(id => id).ToList();
        AssertEqual(supplyShops.Count, remedyGroups.Count, "Punishaar supply shop count");
        AssertEqual(shops.Count, stock.Count, "Punishaar every shop row has stock");
        foreach (int remedy in remedyGroups)
        {
            AssertEqual(true, remedySupplies.ContainsKey(remedy), $"Punishaar remedy group {remedy} has a supply shop");
            AssertEqual(true, supplyShops.Contains(remedySupplies[remedy]), $"Punishaar remedy group {remedy} maps to a Supply Shop row");
        }

        foreach (PunishaarShopTable shop in shops.Values)
        {
            List<int> pool = stock[shop.Id];
            AssertEqual(true, pool.Count > 0, $"Punishaar shop {shop.Id} '{shop.Name}' stock is not empty");
            AssertEqual(pool.Count, pool.Distinct().Count(), $"Punishaar shop {shop.Id} stock has no duplicates");
            foreach (int cardId in pool)
            {
                AssertEqual(true, cards.ContainsKey(cardId) && cards[cardId].IsShow != 0, $"Punishaar shop {shop.Id} stock card {cardId} is showable");
                bool sub = cards[cardId].Type is 3 or 4;
                AssertEqual(true, sub || levels.Contains((cardId, 1)), $"Punishaar shop {shop.Id} stock card {cardId} is obtainable at level 1");
            }
            // Support Card Shops only exist to sell Awareness/Resonance cards, which carry no level rows.
            if (shop.Name.StartsWith("Support Card", StringComparison.Ordinal))
                AssertEqual(true, pool.Any(id => cards[id].Type is 3 or 4), $"Punishaar shop {shop.Id} support stock holds sub cards");
            if (shop.Name.StartsWith("Battle Card", StringComparison.Ordinal))
                AssertEqual(true, pool.All(id => cards[id].Type is 1 or 2), $"Punishaar shop {shop.Id} battle stock holds master cards");
        }

        Dictionary<int, PunishaarStageContentTable> contents = TableReaderV2.Parse<PunishaarStageContentTable>().ToDictionary(row => row.Id);
        Dictionary<int, List<int>> stageContents = TableReaderV2.Parse<PunishaarStageContentGroupTable>()
            .ToDictionary(row => row.StageId, row => row.StageContentIds.Where(id => id > 0).ToList());
        foreach (PunishaarStageGroupTable stage in stages)
        {
            foreach (int contentId in stageContents[stage.StageId])
            {
                PunishaarStageContentTable content = contents[contentId];
                if (content.ContentType != 1) continue;
                AssertEqual(true, pools.ContainsKey(content.ShopGroupId), $"Punishaar stage {stage.StageId} node {contentId} shop pool");
                List<int> pool = pools[content.ShopGroupId];
                AssertEqual(true, pool.Count > 0, $"Punishaar stage {stage.StageId} node {contentId} shop pool is not empty");
                AssertEqual(true, pool.All(id => stock.ContainsKey(id)), $"Punishaar stage {stage.StageId} node {contentId} pool shops have stock");
                AssertEqual(true, pool.All(id => shops[id].Name.StartsWith("Supply", StringComparison.Ordinal) == false),
                    $"Punishaar stage {stage.StageId} node {contentId} draws from regular shops");
            }
        }
    }

    private static void PunishaarRunMechanicsChecks()
    {
        MethodInfo checkResult = PunishaarMethod("CheckFightResult", typeof(XPunishaarFinishFightRequest));
        void Accept(bool win, int color, int endHp, int enemyEndHp, string name)
        {
            checkResult.Invoke(null, [new XPunishaarFinishFightRequest
            {
                IsWin = win, LoseMaxSignalBallColor = color, EndHp = endHp, EnemyEndHp = enemyEndHp
            }]);
        }
        PunishaarExpectCode(20430022,
            () => checkResult.Invoke(null, [new XPunishaarFinishFightRequest { IsWin = true, LoseMaxSignalBallColor = 2 }]),
            "Punishaar win with a lose colour is rejected");
        PunishaarExpectCode(20430024,
            () => checkResult.Invoke(null, [new XPunishaarFinishFightRequest { IsWin = true, EnemyEndHp = 40 }]),
            "Punishaar win with a live enemy is rejected");
        Accept(true, 0, 10, 0, "Punishaar simultaneous death counts as a win");
        PunishaarExpectCode(20430024,
            () => checkResult.Invoke(null, [new XPunishaarFinishFightRequest { IsWin = false, EndHp = 5, EnemyEndHp = 0 }]),
            "Punishaar loss with a live player is rejected");
        PunishaarExpectCode(20430022,
            () => checkResult.Invoke(null, [new XPunishaarFinishFightRequest { IsWin = false, LoseMaxSignalBallColor = 9, EndHp = 0, EnemyEndHp = 30 }]),
            "Punishaar loss with an unknown orb colour is rejected");
        Accept(false, 0, 0, 30, "Punishaar loss with an empty slot is accepted");
        Accept(false, 3, 0, 30, "Punishaar loss with a real orb colour is accepted");

        MethodInfo layout = PunishaarMethod("ValidateLayout", typeof(PunishaarStage));
        PunishaarStage stage = PunishaarFixtureStage(100_102, new PunishaarNode { NodeId = 8, Type = 1, Status = 2 }, 3,
            (10201, 1, 1, 1), (10006, 1, 1, 3), (20001, 1, 2, 4));
        layout.Invoke(null, [stage]);
        // Card 2 is Size 2, so its span [StartPos, StartPos+1] only collides with card 1's cell 1 at StartPos 1.
        stage.TotalMasterCards[2].StartPos = 1;
        PunishaarExpectCode(20430038, () => layout.Invoke(null, [stage]), "Punishaar overlapping layout is rejected");
        stage.TotalMasterCards[2].StartPos = 7;
        PunishaarExpectCode(20430037, () => layout.Invoke(null, [stage]), "Punishaar layout past the grid limit is rejected");
        stage.TotalMasterCards[2].StartPos = 3;
        stage.TotalMasterCards[3].AreaType = 3;
        PunishaarExpectCode(20430041, () => layout.Invoke(null, [stage]), "Punishaar layout in an unknown area is rejected");

        MethodInfo sellPrice = PunishaarMethod("SellPrice", typeof(PunishaarMasterCard));
        AssertEqual(1, (int)sellPrice.Invoke(null, [new PunishaarMasterCard { Id = 1, TemplateId = 10001, Level = 1 }])!, "Punishaar level-1 buyback price");
        AssertEqual(2, (int)sellPrice.Invoke(null, [new PunishaarMasterCard { Id = 1, TemplateId = 10001, Level = 2 }])!, "Punishaar level-2 buyback price");
        AssertEqual(3, (int)sellPrice.Invoke(null, [new PunishaarMasterCard { Id = 1, TemplateId = 10001, Level = 1, SubCardId = 11001 }])!,
            "Punishaar buyback price includes the mounted sub card");

        MethodInfo fillGoods = PunishaarMethod("FillGoods", typeof(PunishaarStage), typeof(PunishaarShopInfo));
        Dictionary<int, PunishaarCardTable> cards = TableReaderV2.Parse<PunishaarCardTable>().ToDictionary(row => row.Id);
        PunishaarShopInfo shop = new()
        {
            CandidateShopIds = [10], SelectedShopId = 10,
            Goods = [new PunishaarGoods { CardId = 10201, Level = 1, IsBought = true }]
        };
        fillGoods.Invoke(null, [PunishaarFixtureStage(100_102, new PunishaarNode { NodeId = 8, Type = 1, Status = 2 }, 3), shop]);
        AssertEqual(true, shop.Goods.Sum(good => cards[good.CardId].Size) <= 8, "Punishaar supply shop fills within the shop area");
        AssertEqual(true, shop.Goods.Count > 0 && shop.Goods.All(good => good is { Level: 1, SubCardId: 0, IsBought: false, Frozen: false }),
            "Punishaar stocked goods are level-1 and unbought");
        shop.Goods[0].Frozen = true;
        int frozenCardId = shop.Goods[0].CardId;
        fillGoods.Invoke(null, [PunishaarFixtureStage(100_102, new PunishaarNode { NodeId = 8, Type = 1, Status = 2 }, 3), shop]);
        AssertEqual(frozenCardId, shop.Goods[0].CardId, "Punishaar frozen goods survive a refill");
        AssertEqual(true, shop.Goods[0].Frozen, "Punishaar frozen goods stay frozen");
    }

    #region session lifecycle

    private sealed record PunishaarPush(string Name, object Payload);

    private sealed class PunishaarWire(LoopbackSessionHarness harness)
    {
        private int packetId = 91_000;
        public int TaskSyncs { get; private set; }
        public int OtherPushes { get; private set; }
        public int LastPacketId { get; private set; }

        public (List<PunishaarPush> Pushes, TResponse Response) Call<TRequest, TResponse>(string requestName, TRequest request)
            where TRequest : notnull =>
            CallWithId<TRequest, TResponse>(packetId, requestName, request);

        // Replays a transport attempt: the module must serve the frozen response, not the action again.
        public (List<PunishaarPush> Pushes, TResponse Response) Retry<TRequest, TResponse>(string requestName, TRequest request)
            where TRequest : notnull =>
            CallWithId<TRequest, TResponse>(LastPacketId, requestName, request);

        public (List<PunishaarPush> Pushes, TResponse Response) CallWithId<TRequest, TResponse>(int id, string requestName, TRequest request)
            where TRequest : notnull
        {
            InvokeRequestHandler(harness, requestName, id, request);
            List<PunishaarPush> pushes = [];
            for (int index = 0; index < 64; index++)
            {
                Packet packet = harness.ReadPacket($"{requestName} packet {index + 1}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    // NotifyTask is sent after the response by the shared task hook; it belongs to the
                    // previous request and is counted instead of joining the mode's push list. Shared
                    // machinery (task claim rewards) may push its own notifies too - they are not the
                    // mode's contract, so they are counted and skipped.
                    if (push.Name == nameof(NotifyTask)) { TaskSyncs++; continue; }
                    if (!push.Name.StartsWith("NotifyPunishaar", StringComparison.Ordinal)) { OtherPushes++; continue; }
                    pushes.Add(new PunishaarPush(push.Name, PunishaarPushPayload(push)));
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, $"{requestName} packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, $"{requestName} response id");
                AssertEqual(requestName[..^"Request".Length] + "Response", response.Name, $"{requestName} response name");
                if (id == packetId) packetId++;
                LastPacketId = id;
                return (pushes, MessagePackSerializer.Deserialize<TResponse>(response.Content));
            }
            throw new InvalidDataException($"{requestName} produced no response within 64 packets.");
        }
    }

    private static object PunishaarPushPayload(Packet.Push push) => push.Name switch
    {
        nameof(NotifyPunishaarGoldChange) => MessagePackSerializer.Deserialize<NotifyPunishaarGoldChange>(push.Content),
        nameof(NotifyPunishaarMasterCardChange) => MessagePackSerializer.Deserialize<NotifyPunishaarMasterCardChange>(push.Content),
        nameof(NotifyPunishaarSubCardChange) => MessagePackSerializer.Deserialize<NotifyPunishaarSubCardChange>(push.Content),
        nameof(NotifyPunishaarRewardResult) => MessagePackSerializer.Deserialize<NotifyPunishaarRewardResult>(push.Content),
        _ => throw new InvalidDataException($"Punishaar sent an unknown push {push.Name}.")
    };

    private static int PunishaarGold(List<PunishaarPush> pushes)
    {
        List<PunishaarPush> gold = pushes.Where(push => push.Name == nameof(NotifyPunishaarGoldChange)).ToList();
        AssertEqual(1, gold.Count, "Punishaar pushes exactly one gold change");
        return ((NotifyPunishaarGoldChange)gold[0].Payload).Gold;
    }

    private static NotifyPunishaarMasterCardChange PunishaarMasterCardPush(List<PunishaarPush> pushes)
    {
        List<PunishaarPush> changes = pushes.Where(push => push.Name == nameof(NotifyPunishaarMasterCardChange)).ToList();
        AssertEqual(1, changes.Count, "Punishaar pushes exactly one master card change");
        return (NotifyPunishaarMasterCardChange)changes[0].Payload;
    }

    private static NotifyPunishaarSubCardChange PunishaarSubCardPush(List<PunishaarPush> pushes)
    {
        List<PunishaarPush> changes = pushes.Where(push => push.Name == nameof(NotifyPunishaarSubCardChange)).ToList();
        AssertEqual(1, changes.Count, "Punishaar pushes exactly one sub card change");
        return (NotifyPunishaarSubCardChange)changes[0].Payload;
    }

    private static NotifyPunishaarRewardResult PunishaarRewards(List<PunishaarPush> pushes)
    {
        List<PunishaarPush> rewards = pushes.Where(push => push.Name == nameof(NotifyPunishaarRewardResult)).ToList();
        AssertEqual(1, rewards.Count, "Punishaar pushes exactly one reward result");
        return (NotifyPunishaarRewardResult)rewards[0].Payload;
    }

    private static PunishaarStage PunishaarFixtureStage(int stageId, PunishaarNode node, int durability,
        params (int CardId, int Level, int AreaType, int StartPos)[] cards)
    {
        PunishaarStage stage = new()
        {
            StageId = stageId,
            CurrentNode = node,
            Gold = 100,
            Durability = durability,
            CurrentRound = 1,
            FightAreaGridLimit = 4,
            BagGridLimit = 4,
            RngState = 0x5EED_1234,
            NextCardId = cards.Length
        };
        for (int index = 0; index < cards.Length; index++)
        {
            (int cardId, int level, int areaType, int startPos) = cards[index];
            stage.TotalMasterCards[index + 1] = new PunishaarMasterCard
            {
                Id = index + 1, TemplateId = cardId, Level = level, AreaType = areaType, StartPos = startPos
            };
        }
        return stage;
    }

    private static Player PunishaarDurable(RecordingMongoCollectionProxy<Player> players) =>
        MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
            players.LastSuccessfulReplacementBson ?? throw new InvalidDataException("Punishaar run saved nothing."));

    // First instant after every authored stage-group window opens. 4.9 moved PunishaarActivity.TimeId to 50200
    // (AscNet missing-calendar policy, unbounded) while its stage groups keep the officially dated TimeId 50201, so
    // the stage windows are the authority the frozen clock must satisfy; the activity window must be open there too.
    private static DateTimeOffset PunishaarFrozenNow(PunishaarActivityTable activity)
    {
        long start = 0;
        foreach (int timeId in TableReaderV2.Parse<PunishaarStageGroupTable>()
            .Where(stage => stage.GroupId == activity.StageGroup && stage.TimeId > 0).Select(stage => stage.TimeId).Distinct())
        {
            if (!ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry stageWindow))
                throw new InvalidDataException($"Punishaar stage TimeId {timeId} has no window in ActivitySchedule.");
            start = Math.Max(start, stageWindow.StartTime); // permanent inherited windows start at 0
        }
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(start + 1);
        AssertEqual(true, ActivityScheduleService.IsOpen(activity.TimeId, now), "Punishaar activity window is open at the frozen clock");
        return now;
    }

    private static void PunishaarSessionLifecycle()
    {
        PunishaarActivityTable activity = TableReaderV2.Parse<PunishaarActivityTable>().Single();
        // The authored parent window for TimeId 50201 is the single authority for this mode: the stage
        // TimeIds inherit it and the task groups follow it. Freeze the module clock inside that window so
        // the check never depends on the machine date, and restore it afterwards.
        FieldInfo clock = PunishaarModuleType.GetField("Clock", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException(PunishaarModuleType.FullName, "Clock");
        Func<DateTimeOffset> previousClock = (Func<DateTimeOffset>)clock.GetValue(null)!;
        DateTimeOffset frozen = PunishaarFrozenNow(activity);
        clock.SetValue(null, (Func<DateTimeOffset>)(() => frozen));
        try
        {
            PunishaarFrozenSessionLifecycle(activity);
        }
        finally
        {
            clock.SetValue(null, previousClock);
        }
    }

    private static void PunishaarFrozenSessionLifecycle(PunishaarActivityTable activity)
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players, out _, out _);
        AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(1_031_001);
        using LoopbackSessionHarness harness = new(character, sessionId: "punishaar-lifecycle");
        PunishaarWire wire = new(harness);
        Dictionary<int, PunishaarCardTable> cards = TableReaderV2.Parse<PunishaarCardTable>().ToDictionary(row => row.Id);
        Dictionary<int, PunishaarStageContentTable> contents = TableReaderV2.Parse<PunishaarStageContentTable>().ToDictionary(row => row.Id);
        Dictionary<(int Type, int Size, int Level), PunishaarCardSaleTable> sales = TableReaderV2.Parse<PunishaarCardSaleTable>()
            .ToDictionary(row => (row.Type, row.Size, row.Level));

        (_, XPunishaarGetDataResponse data) = wire.Call<XPunishaarGetDataRequest, XPunishaarGetDataResponse>(
            nameof(XPunishaarGetDataRequest), new XPunishaarGetDataRequest());
        AssertEqual(0, data.Code, "Punishaar GetData code");
        AssertEqual(activity.Id, data.DataDb!.ActivityId, "Punishaar GetData activity");

        // ---- authored run: every node type of stage 100101 with the template ledger tracked --------
        List<int> authoredNodes = TableReaderV2.Parse<PunishaarStageContentGroupTable>()
            .Single(row => row.StageId == 100_101).StageContentIds.Where(id => id > 0).ToList();
        AssertEqual(0, contents[authoredNodes[0]].EnterGold, "Punishaar first node pays no entry gold");
        (List<PunishaarPush> startPushes, XPunishaarStartStageResponse start) =
            wire.Call<XPunishaarStartStageRequest, XPunishaarStartStageResponse>(nameof(XPunishaarStartStageRequest),
                new XPunishaarStartStageRequest { StageId = 100_101 });
        AssertEqual(0, start.Code, "Punishaar start code");
        AssertEqual(0, startPushes.Count, "Punishaar start pushes nothing before the absolute stage");
        PunishaarStage stage = start.Stage!;
        AssertEqual(4, stage.Gold, "Punishaar starting gold");
        AssertEqual(3, stage.Durability, "Punishaar starting durability");
        AssertEqual(1, stage.CurrentRound, "Punishaar starting round");
        AssertEqual(0, stage.TotalMasterCards.Count, "Punishaar starting deck is empty");
        AssertEqual(4, stage.FightAreaGridLimit, "Punishaar starting fight slots");
        AssertEqual(4, stage.BagGridLimit, "Punishaar starting bag slots");
        AssertEqual(7, stage.CurrentNode.Status, "Punishaar first node is an unselected shop");

        long gold = stage.Gold, earned = 0;
        int fightWins = 0, fightsSeen = 0, eventsSeen = 0, shopsSeen = 0, boughtCards = 0;
        for (int index = 0; index < authoredNodes.Count; index++)
        {
            PunishaarStageContentTable content = contents[authoredNodes[index]];
            AssertEqual(content.Id, stage.CurrentNode.NodeId, $"Punishaar node {index + 1} id");
            AssertEqual(content.ContentType, stage.CurrentNode.Type, $"Punishaar node {index + 1} type");

            switch (content.ContentType)
            {
                case 1:
                {
                    shopsSeen++;
                    AssertEqual(7, stage.CurrentNode.Status, $"Punishaar shop node {content.Id} waits for a pick");
                    PunishaarShopInfo info = stage.CurrentNode.ShopInfo!;
                    AssertEqual(2, info.CandidateShopIds.Count, $"Punishaar shop node {content.Id} candidates");
                    (List<PunishaarPush> selectPushes, XPunishaarSelectShopResponse selected) =
                        wire.Call<XPunishaarSelectShopRequest, XPunishaarSelectShopResponse>(nameof(XPunishaarSelectShopRequest),
                            new XPunishaarSelectShopRequest { ShopId = info.CandidateShopIds[0] });
                    AssertEqual(0, selected.Code, $"Punishaar select shop {content.Id} code");
                    AssertEqual(0, selectPushes.Count, $"Punishaar select shop {content.Id} pushes");
                    AssertEqual(2, selected.Node!.Status, $"Punishaar shop {content.Id} is processing after the pick");
                    AssertEqual(info.CandidateShopIds[0], selected.Node.ShopInfo!.SelectedShopId, $"Punishaar shop {content.Id} keeps the picked shop");
                    AssertEqual(true, selected.Node.ShopInfo.Goods.Count > 0, $"Punishaar shop {content.Id} stocks goods");
                    AssertEqual(true, selected.Node.ShopInfo.Goods.Sum(good => cards[good.CardId].Size) <= 8,
                        $"Punishaar shop {content.Id} stock fits the shop area");
                    stage = new PunishaarStage { CurrentNode = selected.Node!, Gold = stage.Gold };
                    if (boughtCards != 0) break;
                    // Buy the first affordable level-1 size-1 master card the shop offers; the client names
                    // the kept slot (the fight area is still empty because stage 100101 only pays gold).
                    PunishaarGoods? good = selected.Node.ShopInfo.Goods.FirstOrDefault(candidate =>
                        cards[candidate.CardId].Type is 1 or 2 && cards[candidate.CardId].Size == 1
                        && sales[(cards[candidate.CardId].Type, cards[candidate.CardId].Size, candidate.Level)].Buy <= gold);
                    if (good is null) break;
                    int goodIndex = selected.Node.ShopInfo.Goods.IndexOf(good) + 1;
                    int price = sales[(cards[good.CardId].Type, cards[good.CardId].Size, good.Level)].Buy;
                    (List<PunishaarPush> buyPushes, XPunishaarBuyGoodsResponse bought) =
                        wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(nameof(XPunishaarBuyGoodsRequest),
                            new XPunishaarBuyGoodsRequest
                            {
                                Index = goodIndex,
                                CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = 1, MasterCardId = 0, SubCardId = 0 }
                            });
                    AssertEqual(0, bought.Code, $"Punishaar buy at shop {content.Id} code");
                    gold -= price;
                    boughtCards++;
                    AssertEqual((int)gold, PunishaarGold(buyPushes), $"Punishaar buy at shop {content.Id} charges the authored price");
                    PunishaarMasterCard added = PunishaarMasterCardPush(buyPushes).AddedCard!;
                    AssertEqual(good.CardId, added.TemplateId, $"Punishaar bought card template at shop {content.Id}");
                    AssertEqual(1, added.AreaType, $"Punishaar bought card area at shop {content.Id}");
                    AssertEqual(1, added.StartPos, $"Punishaar bought card position at shop {content.Id}");
                    AssertEqual(true, bought.Node!.ShopInfo!.Goods[goodIndex - 1].IsBought, $"Punishaar bought good is marked at shop {content.Id}");
                    Player saved = PunishaarDurable(players);
                    AssertEqual(1, saved.Punishaar.StageSaves[100_101].TotalMasterCards.Count, "Punishaar bought card is durable");
                    int catalog = cards[good.CardId].Type switch { 1 => 1, 2 => 2, 3 => 3, _ => 4 };
                    bool unlocked = catalog switch
                    {
                        1 => saved.Punishaar.CharacterCatalogs.Values.Any(set => set.Catalogs.Contains(good.CardId)),
                        2 => saved.Punishaar.PartnerCatalogs.Values.Any(set => set.Catalogs.Contains(good.CardId)),
                        3 => saved.Punishaar.EquipCatalogs.Contains(good.CardId),
                        _ => saved.Punishaar.ResonanceCatalogs.Contains(good.CardId)
                    };
                    AssertEqual(true, unlocked, $"Punishaar buy at shop {content.Id} unlocks the catalog entry");
                    break;
                }
                case 2:
                {
                    eventsSeen++;
                    PunishaarEventInfo info = stage.CurrentNode.EventInfo!;
                    (List<PunishaarPush> selectPushes, XPunishaarSelectEventResponse selected) =
                        wire.Call<XPunishaarSelectEventRequest, XPunishaarSelectEventResponse>(nameof(XPunishaarSelectEventRequest),
                            new XPunishaarSelectEventRequest { EventId = info.RandomEventIds[0] });
                    AssertEqual(0, selected.Code, $"Punishaar select event {content.Id} code");
                    AssertEqual(0, selectPushes.Count, $"Punishaar select event {content.Id} pushes");
                    AssertEqual(info.RandomEventIds[0], selected.Node!.EventInfo!.SelectedEventId, $"Punishaar select event {content.Id} keeps the pick");
                    PunishaarEventGroupTable group = TableReaderV2.Parse<PunishaarEventGroupTable>().Single(row => row.Id == info.RandomEventIds[0]);
                    PunishaarEventRewardTable reward = TableReaderV2.Parse<PunishaarEventRewardTable>().Single(row => row.Id == group.EventRewardId);
                    (List<PunishaarPush> finishPushes, XPunishaarFinishEventResponse finished) =
                        wire.Call<XPunishaarFinishEventRequest, XPunishaarFinishEventResponse>(nameof(XPunishaarFinishEventRequest),
                            new XPunishaarFinishEventRequest());
                    AssertEqual(0, finished.Code, $"Punishaar finish event {content.Id} code");
                    gold += reward.GoldCount;
                    earned += reward.GoldCount;
                    AssertEqual(reward.GoldCount > 0 ? 1 : 0, finishPushes.Count(push => push.Name == nameof(NotifyPunishaarGoldChange)),
                        $"Punishaar event {content.Id} pays gold only when authored");
                    if (reward.GoldCount > 0) AssertEqual((int)gold, PunishaarGold(finishPushes), $"Punishaar event {content.Id} pays the authored gold");
                    AssertEqual(reward.GoldCount, PunishaarRewards(finishPushes).RewardGoodsList.Where(good => good.RewardType == 3).Sum(good => good.Amount),
                        $"Punishaar event {content.Id} reports the authored gold");
                    if (reward.CardId > 0)
                    {
                        AssertEqual(4, finished.Node!.Status, $"Punishaar event {content.Id} stages its card reward");
                        (List<PunishaarPush> rewardPushes, XPunishaarHandlePendingRewardResponse handled) =
                            wire.Call<XPunishaarHandlePendingRewardRequest, XPunishaarHandlePendingRewardResponse>(
                                nameof(XPunishaarHandlePendingRewardRequest), new XPunishaarHandlePendingRewardRequest
                                {
                                    IsAccept = true,
                                    CardDetail = new PunishaarRewardCardDetailInfo
                                    {
                                        AreaType = 1, StartPos = 2, MasterCardId = 0, SubCardId = 0
                                    }
                                });
                        AssertEqual(0, handled.Code, $"Punishaar pending reward {content.Id} code");
                        AssertEqual(5, handled.Node!.Status, $"Punishaar pending reward {content.Id} finishes the node");
                        AssertEqual(true, rewardPushes.Any(push => push.Name is nameof(NotifyPunishaarMasterCardChange) or nameof(NotifyPunishaarSubCardChange)),
                            $"Punishaar pending reward {content.Id} notifies the granted card");
                    }
                    else AssertEqual(5, finished.Node!.Status, $"Punishaar event {content.Id} finishes the node");
                    break;
                }
                case 3:
                case 4:
                {
                    fightsSeen++;
                    PunishaarNode node = stage.CurrentNode;
                    if (node.Status == 1)
                    {
                        (List<PunishaarPush> selectPushes, XPunishaarSelectFightResponse selected) =
                            wire.Call<XPunishaarSelectFightRequest, XPunishaarSelectFightResponse>(nameof(XPunishaarSelectFightRequest),
                                new XPunishaarSelectFightRequest { FightId = node.FightInfo!.RandomFightIds[0] });
                        AssertEqual(0, selected.Code, $"Punishaar select fight {content.Id} code");
                        AssertEqual(0, selectPushes.Count, $"Punishaar select fight {content.Id} pushes");
                        node = selected.Node!;
                    }
                    AssertEqual(true, node.FightInfo!.SelectedFightId > 0, $"Punishaar fight node {content.Id} has a fight");
                    // The server refuses a result that was never started (the client always calls EnterFight).
                    (_, XPunishaarFinishFightResponse unstarted) = wire.Call<XPunishaarFinishFightRequest, XPunishaarFinishFightResponse>(
                        nameof(XPunishaarFinishFightRequest), new XPunishaarFinishFightRequest { IsWin = true, EnemyEndHp = 0 });
                    AssertEqual(20430020, unstarted.Code, $"Punishaar fight node {content.Id} needs EnterFight first");
                    (List<PunishaarPush> enterPushes, XPunishaarEnterFightResponse entered) =
                        wire.Call<XPunishaarEnterFightRequest, XPunishaarEnterFightResponse>(nameof(XPunishaarEnterFightRequest),
                            new XPunishaarEnterFightRequest());
                    AssertEqual(0, entered.Code, $"Punishaar enter fight {content.Id} code");
                    AssertEqual(0, enterPushes.Count, $"Punishaar enter fight {content.Id} pushes");
                    AssertEqual(2, entered.Node!.Status, $"Punishaar fight node {content.Id} is processing");
                    (List<PunishaarPush> winPushes, XPunishaarFinishFightResponse won) =
                        wire.Call<XPunishaarFinishFightRequest, XPunishaarFinishFightResponse>(nameof(XPunishaarFinishFightRequest),
                            new XPunishaarFinishFightRequest
                            {
                                IsWin = true, LoseMaxSignalBallColor = 0, StartHp = 400, EndHp = 120,
                                EnemyStartHp = 200, EnemyEndHp = -5, FightTime = 20
                            });
                    AssertEqual(0, won.Code, $"Punishaar finish fight {content.Id} code");
                    fightWins++;
                    gold += content.Gold;
                    earned += content.Gold;
                    AssertEqual((int)gold, PunishaarGold(winPushes), $"Punishaar fight {content.Id} pays the authored gold");
                    NotifyPunishaarRewardResult rewardResult = PunishaarRewards(winPushes);
                    AssertEqual(stage.StageId, rewardResult.StageId, $"Punishaar fight {content.Id} reward stage");
                    AssertEqual(250, rewardResult.RewardGoodsList.Single(good => good.RewardType == 4).Amount,
                        $"Punishaar fight {content.Id} reports the hp growth");
                    AssertEqual(true, rewardResult.RewardGoodsList.Any(good => good.RewardType == 5)
                        && rewardResult.RewardGoodsList.Any(good => good.RewardType == 6),
                        $"Punishaar fight {content.Id} reports the slot growth");
                    stage = won.Stage!;
                    AssertEqual(5, stage.CurrentNode.Status, $"Punishaar fight {content.Id} node is finished");
                    AssertEqual(fightWins, stage.FightWinCount, $"Punishaar fight {content.Id} win count");
                    AssertEqual(4 + fightWins, stage.FightAreaGridLimit, $"Punishaar fight {content.Id} fight slots grow");
                    AssertEqual(4 + fightWins, stage.BagGridLimit, $"Punishaar fight {content.Id} bag slots grow");
                    // A second result for the same battle is an ack-loss retry, never a second reward.
                    (List<PunishaarPush> replayPushes, XPunishaarFinishFightResponse replay) =
                        wire.Call<XPunishaarFinishFightRequest, XPunishaarFinishFightResponse>(nameof(XPunishaarFinishFightRequest),
                            new XPunishaarFinishFightRequest { IsWin = true, EnemyEndHp = 0 });
                    AssertEqual(20430021, replay.Code, $"Punishaar fight {content.Id} refuses a repeated result");
                    AssertEqual(0, replayPushes.Count, $"Punishaar fight {content.Id} repeated result pushes nothing");
                    break;
                }
            }

            (List<PunishaarPush> exitPushes, XPunishaarExitNodeResponse exit) =
                wire.Call<XPunishaarExitNodeRequest, XPunishaarExitNodeResponse>(nameof(XPunishaarExitNodeRequest), new XPunishaarExitNodeRequest());
            AssertEqual(0, exit.Code, $"Punishaar exit node {content.Id} code");
            if (index + 1 < authoredNodes.Count)
            {
                int enterGold = contents[authoredNodes[index + 1]].EnterGold;
                gold += enterGold;
                earned += enterGold;
                AssertEqual(enterGold > 0 ? 1 : 0, exitPushes.Count, $"Punishaar exit node {content.Id} entry gold push");
                if (enterGold > 0) AssertEqual((int)gold, PunishaarGold(exitPushes), $"Punishaar exit node {content.Id} pays the next node entry gold");
                stage = exit.Stage!;
                AssertEqual(authoredNodes[index + 1], stage.CurrentNode.NodeId, $"Punishaar exit node {content.Id} advances to the next node");
                AssertEqual(content.Id, stage.HistoryNodeList[^1].NodeId, $"Punishaar exit node {content.Id} archives the node");
                AssertEqual(6, stage.HistoryNodeList[^1].Status, $"Punishaar archived node {content.Id} is exited");
                continue;
            }
            AssertEqual(true, exit.Stage is null, $"Punishaar exit of the last node {content.Id} settles instead of advancing");
            AssertEqual(0, exitPushes.Count, "Punishaar clear pays no extra gold");
            PunishaarSettleInfo settle = exit.SettleInfo!;
            AssertEqual(1, settle.SettleType, "Punishaar clear settle type");
            AssertEqual(100_101, settle.StageId, "Punishaar clear settle stage");
            AssertEqual(fightWins, settle.FightWinCount, "Punishaar clear settle win count");
            AssertEqual((int)earned, settle.AllGold, "Punishaar clear settle reports earned gold only");
            AssertEqual(false, settle.IsNewRecord, "Punishaar normal stage never claims a record");
        }
        AssertEqual(authoredNodes.Count, shopsSeen + eventsSeen + fightsSeen, "Punishaar authored run walked every node");
        AssertEqual(true, fightsSeen > 0 && eventsSeen > 0 && shopsSeen > 0, "Punishaar run covered fights, events and shops");
        AssertEqual(true, wire.TaskSyncs > 0, "Punishaar run syncs the task partial after fights and exits");

        Player durable = PunishaarDurable(players);
        AssertEqual(true, durable.Punishaar.PassedStageIds.Contains(100_101), "Punishaar clear is durable");
        AssertEqual(0, durable.Punishaar.StageSaves.Count, "Punishaar clear removes the save");
        AssertEqual(1, durable.Punishaar.StageChallengeCounts[100_101], "Punishaar clear records the challenge");
        AssertEqual(fightWins, durable.Punishaar.BestWonNodeCounts[100_101 * 10 + 3], "Punishaar clear records the best fight nodes");
        NotifyPunishaarLoginData login = (NotifyPunishaarLoginData)PunishaarMethod("BuildLoginData", typeof(Player), typeof(DateTimeOffset))
            .Invoke(null, [durable, PunishaarFrozenNow(activity)])!;
        AssertEqual(activity.Id, login.ActivityId, "Punishaar login activity");
        AssertEqual(0, login.SaveStageIds.Count, "Punishaar login lists no save after the clear");
        AssertEqual(true, login.PassStageIds.Contains(100_101), "Punishaar login lists the cleared stage");

        // Task claims: the shared claim machinery must accept the authored clear task with its authored
        // reward, must not pay twice, and must reject the endless task whose objective is still unmet
        // (120765 needs 14 choice-fight nodes in stage 100109).
        (_, FinishTaskResponse clearClaim) = wire.Call<FinishTaskRequest, FinishTaskResponse>(
            nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 120756 });
        AssertEqual(0, clearClaim.Code, "Punishaar clear task claim code");
        AssertEqual(true, clearClaim.RewardGoodsList.Count > 0, "Punishaar clear task claim pays the authored reward");
        AssertEqual(true, harness.Session.player.MissionProgress.ClaimedTaskIds.Contains(120756), "Punishaar clear task claim is recorded");
        (_, FinishTaskResponse repeatedClaim) = wire.Call<FinishTaskRequest, FinishTaskResponse>(
            nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 120756 });
        AssertEqual(true, repeatedClaim.Code != 0, "Punishaar clear task cannot be claimed twice");
        AssertEqual(0, repeatedClaim.RewardGoodsList.Count, "Punishaar repeated claim pays nothing");
        (_, FinishTaskResponse unearnedClaim) = wire.Call<FinishTaskRequest, FinishTaskResponse>(
            nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 120765 });
        AssertEqual(true, unearnedClaim.Code != 0, "Punishaar endless task cannot be claimed before the objective");
        AssertEqual(0, unearnedClaim.RewardGoodsList.Count, "Punishaar unearned claim pays nothing");

        PunishaarCraftedStageChecks(harness, wire, players, activity);
        PunishaarEndlessStageChecks(harness, wire, players, activity);

        // The write failed and storage could not be read back either: the outcome stays unknown, so the
        // transport is closed without persisting a copy that may disagree with the document.
        using (LoopbackSessionHarness unresolved = new(CreateDrawCompatibilityCharacter(1_031_002), sessionId: "punishaar-unresolved"))
        {
            AssertEqual(true, Server.Instance.Sessions.TryAdd(unresolved.Session.id, unresolved.Session),
                "Punishaar unresolved save registers the session");
            int attemptedWrites = players.ReplaceOneCalls;
            byte[]? committed = players.LastSuccessfulReplacementBson;
            players.FindResults = null;
            players.ThrowOnReplaceOne = true;
            try
            {
                InvokeRequestHandler(unresolved, nameof(XPunishaarStartStageRequest), 91_777,
                    new XPunishaarStartStageRequest { StageId = 100_101 });
            }
            finally
            {
                players.ThrowOnReplaceOne = false;
            }
            AssertEqual(false, Server.Instance.Sessions.ContainsKey(unresolved.Session.id),
                "Punishaar unresolved save closes the session without persisting the in-memory player");
            AssertEqual(attemptedWrites + 1, players.ReplaceOneCalls, "Punishaar unresolved save attempts exactly one write");
            AssertEqual(true, ReferenceEquals(committed, players.LastSuccessfulReplacementBson),
                "Punishaar unresolved session close commits nothing");
        }
    }

    private static void PunishaarCraftedStageChecks(LoopbackSessionHarness harness, PunishaarWire wire,
        RecordingMongoCollectionProxy<Player> players, PunishaarActivityTable activity)
    {
        Player player = harness.Session.player;
        Dictionary<int, PunishaarCardTable> cards = TableReaderV2.Parse<PunishaarCardTable>().ToDictionary(row => row.Id);
        Dictionary<(int Type, int Size, int Level), PunishaarCardSaleTable> sales = TableReaderV2.Parse<PunishaarCardSaleTable>()
            .ToDictionary(row => (row.Type, row.Size, row.Level));
        PunishaarStageGroupTable group = TableReaderV2.Parse<PunishaarStageGroupTable>().Single(row => row.StageId == 100_102);
        int supplyShop = PunishaarPolicyProperty<Dictionary<int, int>>("SupplyByRemedyGroup")[group.RemedyShop];
        AssertEqual("Supply Shop", TableReaderV2.Parse<PunishaarShopTable>().Single(row => row.Id == supplyShop).Name,
            "Punishaar stage 100102 remedy shop");
        // Stage 100102 content 9 is a fight node whose authored group is 100201.
        PunishaarFightTable fight = TableReaderV2.Parse<PunishaarFightTable>().Single(row => row.GroupId == 100201);

        void Adopt(PunishaarStage stage)
        {
            player.Punishaar = new PlayerPunishaarState
            {
                ActivityId = activity.Id,
                CurrentStageId = 0,
                StageSaves = { [stage.StageId] = stage }
            };
        }

        // The client only ever sends BuyGoods/FreezeGoods/RefreshShop against a Processing shop node.
        PunishaarNode Shop() => new()
        {
            NodeId = 8, Type = 1, Status = 2,
            ShopInfo = new PunishaarShopInfo { CandidateShopIds = [5], SelectedShopId = 5 }
        };

        // ---- buyback, sub-card replacement and placement validation ---------------------------------
        PunishaarStage shopStage = PunishaarFixtureStage(100_102, Shop(), 3, (10201, 1, 1, 1), (10001, 1, 1, 2), (20001, 1, 1, 3));
        shopStage.TotalMasterCards[2].SubCardId = 11002;
        shopStage.CurrentNode.ShopInfo!.Goods =
        [
            new PunishaarGoods { CardId = 10201, Level = 1 },
            new PunishaarGoods { CardId = 10006, Level = 1 },
            new PunishaarGoods { CardId = 11001, Level = 1 }
        ];
        Adopt(shopStage);
        (_, XPunishaarEnterStageResponse resumed) = wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(
            nameof(XPunishaarEnterStageRequest), new XPunishaarEnterStageRequest { StageId = 100_102 });
        AssertEqual(0, resumed.Code, "Punishaar crafted stage resumes");
        AssertEqual(3, resumed.Stage!.TotalMasterCards.Count, "Punishaar crafted deck resumes");
        long gold = resumed.Stage.Gold;

        (List<PunishaarPush> discardPushes, XPunishaarDiscardCardResponse discarded) =
            wire.Call<XPunishaarDiscardCardRequest, XPunishaarDiscardCardResponse>(nameof(XPunishaarDiscardCardRequest),
                new XPunishaarDiscardCardRequest { IsMasterCard = false, MasterCardId = 2 });
        AssertEqual(0, discarded.Code, "Punishaar discard sub card code");
        AssertEqual(0, PunishaarSubCardPush(discardPushes).SubCardId, "Punishaar discard sub card clears the slot");
        (List<PunishaarPush> sellPushes, XPunishaarSellCardResponse sold) = wire.Call<XPunishaarSellCardRequest, XPunishaarSellCardResponse>(
            nameof(XPunishaarSellCardRequest), new XPunishaarSellCardRequest { MasterCardId = 2 });
        AssertEqual(0, sold.Code, "Punishaar sell code");
        gold += sales[(cards[10001].Type, cards[10001].Size, 1)].Sell;
        AssertEqual((int)gold, PunishaarGold(sellPushes), "Punishaar sell pays the card price");
        AssertEqual(2, PunishaarMasterCardPush(sellPushes).RemovedCardIds.Single(), "Punishaar sell removes the card");
        (_, XPunishaarSellCardResponse soldAgain) = wire.Call<XPunishaarSellCardRequest, XPunishaarSellCardResponse>(
            nameof(XPunishaarSellCardRequest), new XPunishaarSellCardRequest { MasterCardId = 2 });
        AssertEqual(20430063, soldAgain.Code, "Punishaar repeated sell is refused");

        // Sub-card goods mount onto a matching host (Awareness needs a Character host) and add their own
        // price to the mounted host's buyback value.
        (List<PunishaarPush> mountPushes, XPunishaarBuyGoodsResponse mounted) = wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(
            nameof(XPunishaarBuyGoodsRequest), new XPunishaarBuyGoodsRequest
            {
                Index = 3, CardDetail = new PunishaarRewardCardDetailInfo { MasterCardId = 3, SubCardId = 0 }
            });
        AssertEqual(0, mounted.Code, "Punishaar sub card purchase code");
        AssertEqual(11001, PunishaarSubCardPush(mountPushes).SubCardId, "Punishaar sub card mount reports the template");
        AssertEqual(3, PunishaarSubCardPush(mountPushes).MasterCardId, "Punishaar sub card mount reports the host");
        gold -= sales[(cards[11001].Type, cards[11001].Size, 1)].Buy;
        AssertEqual((int)gold, PunishaarGold(mountPushes), "Punishaar sub card purchase price");
        (List<PunishaarPush> hostSellPushes, XPunishaarSellCardResponse hostSold) = wire.Call<XPunishaarSellCardRequest, XPunishaarSellCardResponse>(
            nameof(XPunishaarSellCardRequest), new XPunishaarSellCardRequest { MasterCardId = 3 });
        AssertEqual(0, hostSold.Code, "Punishaar sell mounted host code");
        gold += sales[(cards[20001].Type, cards[20001].Size, 1)].Sell + sales[(cards[11001].Type, cards[11001].Size, 1)].Sell;
        AssertEqual((int)gold, PunishaarGold(hostSellPushes), "Punishaar buyback value includes the mounted sub card");

        // Upgrade chain: the kept card is the client's MasterCardId, the level cap comes from CardLevel.
        (List<PunishaarPush> mergePushes, XPunishaarBuyGoodsResponse merged) = wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(
            nameof(XPunishaarBuyGoodsRequest), new XPunishaarBuyGoodsRequest
            {
                Index = 1, CardDetail = new PunishaarRewardCardDetailInfo { MasterCardId = 1, SubCardId = 0 }
            });
        AssertEqual(0, merged.Code, "Punishaar upgrade chain code");
        NotifyPunishaarMasterCardChange merge = PunishaarMasterCardPush(mergePushes);
        AssertEqual(10201, merge.AddedCard!.TemplateId, "Punishaar upgrade keeps the template");
        AssertEqual(2, merge.AddedCard.Level, "Punishaar upgrade raises the level");
        AssertEqual(1, merge.AddedCard.StartPos, "Punishaar upgrade keeps the slot");
        AssertEqual(1, merge.RemovedCardIds.Single(), "Punishaar upgrade consumes the kept card");
        gold -= sales[(cards[10201].Type, cards[10201].Size, 1)].Buy;
        AssertEqual((int)gold, PunishaarGold(mergePushes), "Punishaar upgrade charges the goods price");
        Player afterMerge = PunishaarDurable(players);
        PunishaarStage saved = afterMerge.Punishaar.StageSaves[100_102];
        AssertEqual(1, saved.TotalMasterCards.Count, "Punishaar upgrade collapses the deck");
        AssertEqual(2, saved.TotalMasterCards.Values.Single().Level, "Punishaar upgrade persists the level");
        AssertEqual(true, afterMerge.Punishaar.PartnerCatalogs.ContainsKey(2)
            && afterMerge.Punishaar.PartnerCatalogs[2].Catalogs.Contains(10201), "Punishaar upgrade unlocks the level-2 catalog entry");

        // Placement rules: out of bounds, overlap and an unknown area are rejected without any mutation.
        (_, XPunishaarBuyGoodsResponse outOfBounds) = wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(
            nameof(XPunishaarBuyGoodsRequest), new XPunishaarBuyGoodsRequest
            {
                Index = 2, CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = 99 }
            });
        AssertEqual(20430037, outOfBounds.Code, "Punishaar purchase rejects an out of bounds slot");
        (_, XPunishaarBuyGoodsResponse overlap) = wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(
            nameof(XPunishaarBuyGoodsRequest), new XPunishaarBuyGoodsRequest
            {
                Index = 2, CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = 1 }
            });
        AssertEqual(20430038, overlap.Code, "Punishaar purchase rejects an overlapping slot");
        (_, XPunishaarBuyGoodsResponse badArea) = wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(
            nameof(XPunishaarBuyGoodsRequest), new XPunishaarBuyGoodsRequest
            {
                Index = 2, CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 9, StartPos = 2 }
            });
        AssertEqual(20430041, badArea.Code, "Punishaar purchase rejects an unknown area");
        (_, XPunishaarBuyGoodsResponse noDetail) = wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(
            nameof(XPunishaarBuyGoodsRequest), new XPunishaarBuyGoodsRequest { Index = 2 });
        AssertEqual(20430036, noDetail.Code, "Punishaar purchase needs the client card detail");
        (_, XPunishaarBuyGoodsResponse boughtTwice) = wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(
            nameof(XPunishaarBuyGoodsRequest), new XPunishaarBuyGoodsRequest
            {
                Index = 1, CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = 2 }
            });
        AssertEqual(20430066, boughtTwice.Code, "Punishaar purchase refuses an already bought slot");
        AssertEqual((int)gold, PunishaarDurable(players).Punishaar.StageSaves[100_102].Gold, "Punishaar rejected purchases cost nothing");

        // Freeze + refresh keep frozen goods and charge the authored formula.
        (List<PunishaarPush> freezePushes, XPunishaarFreezeGoodsResponse frozen) = wire.Call<XPunishaarFreezeGoodsRequest, XPunishaarFreezeGoodsResponse>(
            nameof(XPunishaarFreezeGoodsRequest), new XPunishaarFreezeGoodsRequest { Index = 2, IsFreeze = true });
        AssertEqual(0, frozen.Code, "Punishaar freeze code");
        AssertEqual(0, freezePushes.Count, "Punishaar freeze pushes nothing");
        AssertEqual(true, frozen.Node!.ShopInfo!.Goods[1].Frozen, "Punishaar freeze marks the slot");
        (List<PunishaarPush> refreshPushes, XPunishaarRefreshShopResponse refreshed) = wire.Call<XPunishaarRefreshShopRequest, XPunishaarRefreshShopResponse>(
            nameof(XPunishaarRefreshShopRequest), new XPunishaarRefreshShopRequest());
        AssertEqual(0, refreshed.Code, "Punishaar refresh code");
        gold -= 1;
        AssertEqual((int)gold, PunishaarGold(refreshPushes), "Punishaar refresh charges the authored base cost");
        AssertEqual(10006, refreshed.Node!.ShopInfo!.Goods[0].CardId, "Punishaar refresh keeps the frozen slot first");
        AssertEqual(true, refreshed.Node.ShopInfo.Goods[0].Frozen, "Punishaar refresh keeps the frozen flag");
        AssertEqual(1, refreshed.Node.ShopInfo.RefreshTimes, "Punishaar refresh counts the reroll");
        (List<PunishaarPush> secondRefreshPushes, XPunishaarRefreshShopResponse refreshedAgain) =
            wire.Call<XPunishaarRefreshShopRequest, XPunishaarRefreshShopResponse>(nameof(XPunishaarRefreshShopRequest), new XPunishaarRefreshShopRequest());
        AssertEqual(0, refreshedAgain.Code, "Punishaar second refresh code");
        gold -= 2;
        AssertEqual((int)gold, PunishaarGold(secondRefreshPushes), "Punishaar refresh cost grows with the reroll count");
        AssertEqual(2, refreshedAgain.Node!.ShopInfo!.RefreshTimes, "Punishaar second refresh counts the reroll");

        // ---- event reward replacement: accept with a placement, then abandon ------------------------
        int cardEventGroup = TableReaderV2.Parse<PunishaarEventGroupTable>()
            .Where(row => row.GroupId == 10201 && TableReaderV2.Parse<PunishaarEventRewardTable>().Any(reward => reward.Id == row.EventRewardId && reward.CardId > 0))
            .Select(row => row.Id).First();
        PunishaarNode eventNode = new()
        {
            NodeId = 10, Type = 2, Status = 2,
            EventInfo = new PunishaarEventInfo { RandomEventIds = [cardEventGroup], SelectedEventId = cardEventGroup }
        };
        PunishaarStage eventStage = PunishaarFixtureStage(100_102, eventNode, 3, (10001, 1, 1, 1));
        Adopt(eventStage);
        wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(nameof(XPunishaarEnterStageRequest),
            new XPunishaarEnterStageRequest { StageId = 100_102 });
        (_, XPunishaarFinishEventResponse cardEvent) = wire.Call<XPunishaarFinishEventRequest, XPunishaarFinishEventResponse>(
            nameof(XPunishaarFinishEventRequest), new XPunishaarFinishEventRequest());
        AssertEqual(0, cardEvent.Code, "Punishaar card event code");
        AssertEqual(4, cardEvent.Node!.Status, "Punishaar card event waits for the card decision");
        AssertEqual(PunishaarDurable(players).Punishaar.StageSaves[100_102].CurrentNode.PendingRewardCardId, cardEvent.Node.PendingRewardCardId,
            "Punishaar pending reward card is durable");
        (List<PunishaarPush> pendingPushes, XPunishaarHandlePendingRewardResponse placed) =
            wire.Call<XPunishaarHandlePendingRewardRequest, XPunishaarHandlePendingRewardResponse>(nameof(XPunishaarHandlePendingRewardRequest),
                new XPunishaarHandlePendingRewardRequest
                {
                    IsAccept = true,
                    CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = 2, MasterCardId = 0, SubCardId = 0 }
                });
        AssertEqual(0, placed.Code, "Punishaar pending reward placement code");
        AssertEqual(5, placed.Node!.Status, "Punishaar pending reward placement finishes the node");
        AssertEqual(cardEvent.Node.PendingRewardCardId, PunishaarMasterCardPush(pendingPushes).AddedCard!.TemplateId,
            "Punishaar pending reward grants the staged card");
        AssertEqual(PunishaarDurable(players).Punishaar.StageSaves[100_102].TotalMasterCards.Count, 2, "Punishaar pending reward card is durable");

        PunishaarStage abandonStage = PunishaarFixtureStage(100_102, new PunishaarNode
        {
            NodeId = 10, Type = 2, Status = 2,
            EventInfo = new PunishaarEventInfo { RandomEventIds = [cardEventGroup], SelectedEventId = cardEventGroup }
        }, 3, (10001, 1, 1, 1));
        Adopt(abandonStage);
        wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(nameof(XPunishaarEnterStageRequest),
            new XPunishaarEnterStageRequest { StageId = 100_102 });
        wire.Call<XPunishaarFinishEventRequest, XPunishaarFinishEventResponse>(nameof(XPunishaarFinishEventRequest), new XPunishaarFinishEventRequest());
        (List<PunishaarPush> abandonPushes, XPunishaarHandlePendingRewardResponse abandoned) =
            wire.Call<XPunishaarHandlePendingRewardRequest, XPunishaarHandlePendingRewardResponse>(nameof(XPunishaarHandlePendingRewardRequest),
                new XPunishaarHandlePendingRewardRequest { IsAccept = false });
        AssertEqual(0, abandoned.Code, "Punishaar pending reward abandon code");
        AssertEqual(5, abandoned.Node!.Status, "Punishaar pending reward abandon finishes the node");
        AssertEqual(0, abandonPushes.Count, "Punishaar pending reward abandon grants nothing");
        AssertEqual(1, PunishaarDurable(players).Punishaar.StageSaves[100_102].TotalMasterCards.Count, "Punishaar abandoned card is not durable");

        // ---- full card layout submission -------------------------------------------------------------
        PunishaarStage layoutStage = PunishaarFixtureStage(100_102, Shop(), 3, (10201, 1, 1, 1), (10006, 1, 1, 3));
        Adopt(layoutStage);
        wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(nameof(XPunishaarEnterStageRequest),
            new XPunishaarEnterStageRequest { StageId = 100_102 });
        (_, XPunishaarSetCardPosResponse moved) = wire.Call<XPunishaarSetCardPosRequest, XPunishaarSetCardPosResponse>(
            nameof(XPunishaarSetCardPosRequest), new XPunishaarSetCardPosRequest
            {
                CardPosList =
                [
                    new PunishaarCardPosInfo { Id = 1, AreaType = 2, StartPos = 1 },
                    new PunishaarCardPosInfo { Id = 2, AreaType = 1, StartPos = 3 }
                ]
            });
        AssertEqual(0, moved.Code, "Punishaar layout submission code");
        AssertEqual(2, PunishaarDurable(players).Punishaar.StageSaves[100_102].TotalMasterCards[1].AreaType, "Punishaar layout persists the area");
        (_, XPunishaarSetCardPosResponse partial) = wire.Call<XPunishaarSetCardPosRequest, XPunishaarSetCardPosResponse>(
            nameof(XPunishaarSetCardPosRequest), new XPunishaarSetCardPosRequest
            {
                CardPosList = [new PunishaarCardPosInfo { Id = 1, AreaType = 1, StartPos = 1 }]
            });
        AssertEqual(20430034, partial.Code, "Punishaar layout requires every card");
        (_, XPunishaarSetCardPosResponse overlapping) = wire.Call<XPunishaarSetCardPosRequest, XPunishaarSetCardPosResponse>(
            nameof(XPunishaarSetCardPosRequest), new XPunishaarSetCardPosRequest
            {
                CardPosList =
                [
                    new PunishaarCardPosInfo { Id = 1, AreaType = 1, StartPos = 1 },
                    new PunishaarCardPosInfo { Id = 2, AreaType = 1, StartPos = 1 }
                ]
            });
        AssertEqual(20430038, overlapping.Code, "Punishaar layout rejects overlaps");

        // ---- remedy shop after a defeat, then a rematch and the durability end ----------------------
        PunishaarNode fightNode = new()
        {
            NodeId = 9, Type = 3, Status = 2,
            FightInfo = new PunishaarFightInfo { RandomFightIds = [fight.Id], SelectedFightId = fight.Id }
        };
        PunishaarStage fightStage = PunishaarFixtureStage(100_102, fightNode, 3, (10201, 1, 1, 1));
        Adopt(fightStage);
        wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(nameof(XPunishaarEnterStageRequest),
            new XPunishaarEnterStageRequest { StageId = 100_102 });
        (_, XPunishaarEnterFightResponse started) = wire.Call<XPunishaarEnterFightRequest, XPunishaarEnterFightResponse>(
            nameof(XPunishaarEnterFightRequest), new XPunishaarEnterFightRequest());
        AssertEqual(0, started.Code, "Punishaar enter fight code");
        (List<PunishaarPush> lossPushes, XPunishaarFinishFightResponse lost) = wire.Call<XPunishaarFinishFightRequest, XPunishaarFinishFightResponse>(
            nameof(XPunishaarFinishFightRequest), new XPunishaarFinishFightRequest
            {
                IsWin = false, LoseMaxSignalBallColor = 1, StartHp = 400, EndHp = 0, EnemyStartHp = 200, EnemyEndHp = 60
            });
        AssertEqual(0, lost.Code, "Punishaar defeat code");
        AssertEqual(3, lost.Stage!.CurrentNode.Status, "Punishaar defeat opens the remedy shop");
        AssertEqual(supplyShop, lost.Stage.CurrentNode.ShopInfo!.SelectedShopId, "Punishaar defeat opens the authored supply shop");
        AssertEqual(2, lost.Stage.Durability, "Punishaar defeat costs one durability");
        AssertEqual(100 + group.RemedyGold, PunishaarGold(lossPushes), "Punishaar defeat pays the remedy gold");
        AssertEqual(group.RemedyGold, PunishaarRewards(lossPushes).RewardGoodsList.Single(good => good.RewardType == 3).Amount,
            "Punishaar defeat reports the remedy gold");
        (List<PunishaarPush> remedySellPushes, XPunishaarSellCardResponse remedySell) = wire.Call<XPunishaarSellCardRequest, XPunishaarSellCardResponse>(
            nameof(XPunishaarSellCardRequest), new XPunishaarSellCardRequest { MasterCardId = 1 });
        AssertEqual(0, remedySell.Code, "Punishaar sells cards in the remedy shop");
        AssertEqual(100 + group.RemedyGold + sales[(cards[10201].Type, cards[10201].Size, 1)].Sell, PunishaarGold(remedySellPushes),
            "Punishaar remedy sale pays the buyback price");
        (_, XPunishaarEnterFightResponse left) = wire.Call<XPunishaarEnterFightRequest, XPunishaarEnterFightResponse>(
            nameof(XPunishaarEnterFightRequest), new XPunishaarEnterFightRequest());
        AssertEqual(0, left.Code, "Punishaar leaves the remedy shop");
        AssertEqual(2, left.Node!.Status, "Punishaar remedy shop exit returns to the fight");
        AssertEqual(true, left.Node.ShopInfo is null, "Punishaar remedy shop exit clears the shop");
        wire.Call<XPunishaarEnterFightRequest, XPunishaarEnterFightResponse>(nameof(XPunishaarEnterFightRequest), new XPunishaarEnterFightRequest());
        (_, XPunishaarFinishFightResponse wonAgain) = wire.Call<XPunishaarFinishFightRequest, XPunishaarFinishFightResponse>(
            nameof(XPunishaarFinishFightRequest), new XPunishaarFinishFightRequest
            {
                IsWin = true, LoseMaxSignalBallColor = 0, StartHp = 400, EndHp = 90, EnemyStartHp = 200, EnemyEndHp = 0
            });
        AssertEqual(0, wonAgain.Code, "Punishaar rematch code");
        AssertEqual(5, wonAgain.Stage!.CurrentNode.Status, "Punishaar rematch finishes the node");
        AssertEqual(2, wonAgain.Stage.Durability, "Punishaar rematch keeps the remaining durability");

        PunishaarStage lastStage = PunishaarFixtureStage(100_102, new PunishaarNode
        {
            NodeId = 9, Type = 3, Status = 2,
            FightInfo = new PunishaarFightInfo { RandomFightIds = [fight.Id], SelectedFightId = fight.Id }
        }, 1, (10201, 1, 1, 1));
        Adopt(lastStage);
        wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(nameof(XPunishaarEnterStageRequest),
            new XPunishaarEnterStageRequest { StageId = 100_102 });
        (_, XPunishaarFinishFightResponse unstarted) = wire.Call<XPunishaarFinishFightRequest, XPunishaarFinishFightResponse>(
            nameof(XPunishaarFinishFightRequest), new XPunishaarFinishFightRequest { IsWin = false, EndHp = 0, EnemyEndHp = 50 });
        AssertEqual(20430020, unstarted.Code, "Punishaar durability end needs a started battle");
        wire.Call<XPunishaarEnterFightRequest, XPunishaarEnterFightResponse>(nameof(XPunishaarEnterFightRequest), new XPunishaarEnterFightRequest());
        (List<PunishaarPush> endPushes, XPunishaarFinishFightResponse durableEnd) = wire.Call<XPunishaarFinishFightRequest, XPunishaarFinishFightResponse>(
            nameof(XPunishaarFinishFightRequest), new XPunishaarFinishFightRequest
            {
                IsWin = false, LoseMaxSignalBallColor = 3, StartHp = 400, EndHp = 0, EnemyStartHp = 200, EnemyEndHp = 50
            });
        AssertEqual(0, durableEnd.Code, "Punishaar durability end code");
        AssertEqual(0, endPushes.Count, "Punishaar durability end pushes nothing");
        AssertEqual(2, durableEnd.SettleInfo!.SettleType, "Punishaar durability end settle type");
        AssertEqual(0, durableEnd.SettleInfo.Durability, "Punishaar durability end reports zero durability");
        AssertEqual(1, durableEnd.SettleInfo.CurrentRound, "Punishaar durability end reports the round");
        (_, XPunishaarEnterStageResponse gone) = wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(
            nameof(XPunishaarEnterStageRequest), new XPunishaarEnterStageRequest { StageId = 100_102 });
        AssertEqual(20430004, gone.Code, "Punishaar settled stage has no save");

        // ---- a failed save whose write never landed: the stored document proves it, so the live state is
        // adopted from it and a later retry of the same packet id must not buy twice ---------------------
        PunishaarStage retryStage = PunishaarFixtureStage(100_102, Shop(), 3, (10201, 1, 1, 1));
        retryStage.CurrentNode.ShopInfo!.Goods = [new PunishaarGoods { CardId = 10201, Level = 1 }];
        Adopt(retryStage);
        wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(nameof(XPunishaarEnterStageRequest),
            new XPunishaarEnterStageRequest { StageId = 100_102 });
        XPunishaarBuyGoodsRequest retryBuy = new()
        {
            Index = 1, CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = 2 }
        };
        // The stored document still holds the pre-purchase state: the read proves the write never landed.
        players.FindResults = [BsonSerializer.Deserialize<Player>(player.ToBson())];
        players.ThrowOnReplaceOne = true;
        (List<PunishaarPush> failedPushes, XPunishaarBuyGoodsResponse failedBuy) =
            wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(nameof(XPunishaarBuyGoodsRequest), retryBuy);
        AssertEqual(1, failedBuy.Code, "Punishaar answers an unlanded save with Code 1");
        AssertEqual(0, failedPushes.Count, "Punishaar save failure pushes nothing");
        AssertEqual(100, player.Punishaar.StageSaves[100_102].Gold, "Punishaar save failure adopts the durable gold");
        AssertEqual(false, player.Punishaar.StageSaves[100_102].CurrentNode.ShopInfo!.Goods[0].IsBought,
            "Punishaar save failure adopts the durable stock");
        (_, XPunishaarBuyGoodsResponse indexGuard) = wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(
            nameof(XPunishaarBuyGoodsRequest), new XPunishaarBuyGoodsRequest
            {
                Index = 99, CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = 2 }
            });
        AssertEqual(20430058, indexGuard.Code, "Punishaar rejects a missing slot without touching the save");
        players.ThrowOnReplaceOne = false;
        players.FindResults = null;
        (List<PunishaarPush> boughtPushes, XPunishaarBuyGoodsResponse boughtAfterFailure) =
            wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(nameof(XPunishaarBuyGoodsRequest), retryBuy);
        AssertEqual(0, boughtAfterFailure.Code, "Punishaar purchase succeeds once the save recovers");
        AssertEqual(98, PunishaarGold(boughtPushes), "Punishaar recovered purchase charges once");
        (List<PunishaarPush> replayPushes, XPunishaarBuyGoodsResponse replayBuy) = wire.Retry<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(
            nameof(XPunishaarBuyGoodsRequest), retryBuy);
        AssertEqual(0, replayBuy.Code, "Punishaar replays the frozen response for a retried packet id");
        AssertEqual(98, PunishaarGold(replayPushes), "Punishaar replay repeats the frozen pushes");
        PunishaarStage replayed = PunishaarDurable(players).Punishaar.StageSaves[100_102];
        AssertEqual(2, replayed.TotalMasterCards.Count, "Punishaar replay does not buy the card twice");
        AssertEqual(98, replayed.Gold, "Punishaar replay does not charge twice");

        // ---- acknowledgement loss on a paid purchase: the committed document is read back and adopted, so
        // a later unrelated save cannot erase the purchase and a repeated packet id cannot buy twice ------
        PunishaarStage ackStage = PunishaarFixtureStage(100_102, Shop(), 3, (10201, 1, 1, 1));
        ackStage.CurrentNode.ShopInfo!.Goods =
        [
            new PunishaarGoods { CardId = 10201, Level = 1 }, new PunishaarGoods { CardId = 10006, Level = 1 }
        ];
        Adopt(ackStage);
        wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(nameof(XPunishaarEnterStageRequest),
            new XPunishaarEnterStageRequest { StageId = 100_102 });
        long ackGold = player.Punishaar.StageSaves[100_102].Gold;
        int ackCards = player.Punishaar.StageSaves[100_102].TotalMasterCards.Count;
        int ackPrice = sales[(cards[10201].Type, cards[10201].Size, 1)].Buy;
        XPunishaarBuyGoodsRequest ackBuy = new()
        {
            Index = 1, CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = 2 }
        };
        // The replacement lands inside the collection and only its acknowledgement is lost, so the durable
        // read serves exactly the document the write produced.
        players.BeforeReplaceOne = document => players.FindResults = [BsonSerializer.Deserialize<Player>(document.ToBson())];
        players.ThrowAfterReplaceOne = true;
        (List<PunishaarPush> ackPushes, XPunishaarBuyGoodsResponse acked) =
            wire.Call<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(nameof(XPunishaarBuyGoodsRequest), ackBuy);
        players.BeforeReplaceOne = null;
        players.FindResults = null;
        int ackPacketId = wire.LastPacketId;
        AssertEqual(0, acked.Code, "Punishaar committed purchase answers success after the acknowledgement loss");
        AssertEqual((int)(ackGold - ackPrice), PunishaarGold(ackPushes), "Punishaar committed purchase charges once");
        AssertEqual(10201, PunishaarMasterCardPush(ackPushes).AddedCard!.TemplateId, "Punishaar committed purchase reports the added card");
        AssertEqual((int)(ackGold - ackPrice), player.Punishaar.StageSaves[100_102].Gold,
            "Punishaar committed purchase is adopted instead of rolled back");
        AssertEqual(ackCards + 1, player.Punishaar.StageSaves[100_102].TotalMasterCards.Count, "Punishaar committed purchase is adopted once");
        AssertEqual(true, player.Punishaar.StageSaves[100_102].CurrentNode.ShopInfo!.Goods[0].IsBought,
            "Punishaar committed purchase is adopted into the stock");

        // Any later whole-player write (here the disconnect save path) persists the adopted purchase.
        int unrelatedWrites = players.ReplaceOneCalls;
        harness.Session.player.Save();
        AssertEqual(unrelatedWrites + 1, players.ReplaceOneCalls, "Punishaar unrelated save writes the live player once");
        Player unrelated = PunishaarDurable(players);
        AssertEqual((int)(ackGold - ackPrice), unrelated.Punishaar.StageSaves[100_102].Gold,
            "Punishaar unrelated save cannot erase the committed purchase");
        AssertEqual(ackCards + 1, unrelated.Punishaar.StageSaves[100_102].TotalMasterCards.Count,
            "Punishaar unrelated save keeps exactly the one purchased card");

        // The identical retried packet id replays the frozen answer: no second charge, no second card.
        int replayWrites = players.ReplaceOneCalls;
        (List<PunishaarPush> ackReplayPushes, XPunishaarBuyGoodsResponse ackReplay) =
            wire.Retry<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(nameof(XPunishaarBuyGoodsRequest), ackBuy);
        AssertEqual(0, ackReplay.Code, "Punishaar acknowledgement-loss retry replays the frozen answer");
        AssertEqual(replayWrites, players.ReplaceOneCalls, "Punishaar acknowledgement-loss retry saves nothing");
        AssertEqual((int)(ackGold - ackPrice), PunishaarGold(ackReplayPushes),
            "Punishaar acknowledgement-loss retry repeats the frozen gold");
        PunishaarStage ackReload = PunishaarDurable(players).Punishaar.StageSaves[100_102];
        AssertEqual((int)(ackGold - ackPrice), ackReload.Gold, "Punishaar acknowledgement-loss reload keeps the charged gold");
        AssertEqual(ackCards + 1, ackReload.TotalMasterCards.Count, "Punishaar acknowledgement-loss reload keeps the one bought card");
        AssertEqual(true, ackReload.CurrentNode.ShopInfo!.Goods[0].IsBought, "Punishaar acknowledgement-loss reload keeps the stock");

        // One id carrying a different request is not the attempt the frozen bytes belong to: it is refused
        // rather than answered with another action's acknowledgement or executed under a used id.
        int reuseWrites = players.ReplaceOneCalls;
        (List<PunishaarPush> reusedPushes, XPunishaarBuyGoodsResponse reusedBuy) =
            wire.CallWithId<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(ackPacketId, nameof(XPunishaarBuyGoodsRequest),
                new XPunishaarBuyGoodsRequest
                {
                    Index = 2, CardDetail = new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = 3 }
                });
        AssertEqual(1, reusedBuy.Code, "Punishaar refuses a packet id carrying a different request");
        AssertEqual(0, reusedPushes.Count, "Punishaar reused packet id pushes nothing");
        AssertEqual(reuseWrites, players.ReplaceOneCalls, "Punishaar reused packet id writes nothing");
        AssertEqual(false, PunishaarDurable(players).Punishaar.StageSaves[100_102].CurrentNode.ShopInfo!.Goods[1].IsBought,
            "Punishaar reused packet id buys nothing");

        // A superseded id is refused too, even as the exact same request: replaying the retired snapshot
        // would rewind the client model over the later action, and re-running it would pay a second time.
        wire.Call<XPunishaarGetDataRequest, XPunishaarGetDataResponse>(nameof(XPunishaarGetDataRequest), new XPunishaarGetDataRequest());
        int retiredWrites = players.ReplaceOneCalls;
        (List<PunishaarPush> retiredPushes, XPunishaarBuyGoodsResponse retiredReplay) =
            wire.CallWithId<XPunishaarBuyGoodsRequest, XPunishaarBuyGoodsResponse>(ackPacketId, nameof(XPunishaarBuyGoodsRequest), ackBuy);
        AssertEqual(1, retiredReplay.Code, "Punishaar refuses a superseded packet id");
        AssertEqual(0, retiredPushes.Count, "Punishaar superseded packet id pushes nothing");
        AssertEqual(retiredWrites, players.ReplaceOneCalls, "Punishaar superseded packet id writes nothing");
        PunishaarStage afterRetired = PunishaarDurable(players).Punishaar.StageSaves[100_102];
        AssertEqual((int)(ackGold - ackPrice), afterRetired.Gold, "Punishaar superseded packet id charges nothing");
        AssertEqual(ackCards + 1, afterRetired.TotalMasterCards.Count, "Punishaar superseded packet id adds no card");

        // ---- away/quit: the save survives an away, and a quit settles it ------------------------------
        (_, XPunishaarAwayStageResponse away) = wire.Call<XPunishaarAwayStageRequest, XPunishaarAwayStageResponse>(
            nameof(XPunishaarAwayStageRequest), new XPunishaarAwayStageRequest { StageId = 100_102 });
        AssertEqual(0, away.Code, "Punishaar away code");
        AssertEqual(0, PunishaarDurable(players).Punishaar.CurrentStageId, "Punishaar away clears the current stage");
        (_, XPunishaarEnterStageResponse back) = wire.Call<XPunishaarEnterStageRequest, XPunishaarEnterStageResponse>(
            nameof(XPunishaarEnterStageRequest), new XPunishaarEnterStageRequest { StageId = 100_102 });
        AssertEqual(0, back.Code, "Punishaar resumes an away save");
        AssertEqual(2, back.Stage!.TotalMasterCards.Count, "Punishaar away save keeps the deck");
        (List<PunishaarPush> quitPushes, XPunishaarQuitStageResponse quit) = wire.Call<XPunishaarQuitStageRequest, XPunishaarQuitStageResponse>(
            nameof(XPunishaarQuitStageRequest), new XPunishaarQuitStageRequest());
        AssertEqual(0, quit.Code, "Punishaar quit code");
        AssertEqual(3, quit.SettleInfo!.SettleType, "Punishaar quit settle type");
        AssertEqual(0, quitPushes.Count, "Punishaar quit pushes nothing");
        Player afterQuit = PunishaarDurable(players);
        AssertEqual(0, afterQuit.Punishaar.StageSaves.Count, "Punishaar quit removes the save");
        // Only StartStage counts a challenge; resuming a crafted save must not fake one.
        AssertEqual(0, afterQuit.Punishaar.StageChallengeCounts.GetValueOrDefault(100_102), "Punishaar resume does not count a challenge");
    }

    private static void PunishaarEndlessStageChecks(LoopbackSessionHarness harness, PunishaarWire wire,
        RecordingMongoCollectionProxy<Player> players, PunishaarActivityTable activity)
    {
        Player player = harness.Session.player;
        Dictionary<int, PunishaarCardTable> cards = TableReaderV2.Parse<PunishaarCardTable>().ToDictionary(row => row.Id);
        HashSet<(int CardId, int Level)> levels = TableReaderV2.Parse<PunishaarCardLevelTable>().Select(row => (row.CardId, row.Level)).ToHashSet();
        PunishaarStageGroupTable endless = TableReaderV2.Parse<PunishaarStageGroupTable>().Single(row => row.Type == 2);
        int maxRound = TableReaderV2.Parse<PunishaarConfigTable>().Single(row => row.Key == "EndlessMaxRound").Param[0];
        AssertEqual(true, maxRound > 1, "Punishaar endless stage is authored with more than one round");

        // The endless stage is unlocked by clearing the previous one; seed the durable progress and run
        // one authored round so the round rollover, the best-round record and the final settlement are all
        // exercised through the real handlers.
        player.Punishaar = new PlayerPunishaarState
        {
            ActivityId = activity.Id,
            CurrentStageId = 0,
            PassedStageIds = TableReaderV2.Parse<PunishaarStageGroupTable>()
                .Where(row => row.StageId < endless.StageId).Select(row => row.StageId).ToList()
        };
        (_, XPunishaarStartStageResponse start) = wire.Call<XPunishaarStartStageRequest, XPunishaarStartStageResponse>(
            nameof(XPunishaarStartStageRequest), new XPunishaarStartStageRequest { StageId = endless.StageId });
        AssertEqual(0, start.Code, "Punishaar endless stage starts");
        PunishaarStage stage = start.Stage!;
        AssertEqual(1, stage.CurrentRound, "Punishaar endless stage starts in round 1");
        AssertEqual(0, stage.HistoryNodeList.Count, "Punishaar endless stage starts with an empty round history");

        List<int> authoredNodes = TableReaderV2.Parse<PunishaarStageContentGroupTable>()
            .Single(row => row.StageId == endless.StageId).StageContentIds.Where(id => id > 0).ToList();
        Dictionary<int, PunishaarStageContentTable> contents = TableReaderV2.Parse<PunishaarStageContentTable>().ToDictionary(row => row.Id);

        // Mirrors the client producer (xpunishaargamecontrolnodeflow.lua _TryUpgradePendingReward then
        // _FindPlacementForDirectBuy): upgrade a held same-level copy when a next level exists, else take
        // the first free fight-area slot that fits the card.
        PunishaarRewardCardDetailInfo RewardDetail(int cardId, int level)
        {
            PunishaarStage durable = PunishaarDurable(players).Punishaar.StageSaves[endless.StageId];
            PunishaarMasterCard? owned = durable.TotalMasterCards.Values
                .FirstOrDefault(card => card.TemplateId == cardId && card.Level == level);
            if (owned is not null && levels.Contains((cardId, level + 1)))
                return new PunishaarRewardCardDetailInfo { MasterCardId = owned.Id, SubCardId = 0 };
            bool[] used = new bool[durable.FightAreaGridLimit + 1];
            foreach (PunishaarMasterCard card in durable.TotalMasterCards.Values.Where(card => card.AreaType == 1))
                for (int position = card.StartPos; position < card.StartPos + cards[card.TemplateId].Size && position < used.Length; position++)
                    used[position] = true;
            for (int position = 1; position + cards[cardId].Size - 1 <= durable.FightAreaGridLimit; position++)
            {
                bool free = true;
                for (int offset = 0; offset < cards[cardId].Size; offset++) free &= !used[position + offset];
                if (free) return new PunishaarRewardCardDetailInfo { AreaType = 1, StartPos = position, MasterCardId = 0, SubCardId = 0 };
            }
            throw new InvalidDataException($"Punishaar endless fixture has no free slot for card {cardId}.");
        }

        int wins = 0, events = 0;
        int authoredEvents = authoredNodes.Count(id => contents[id].ContentType == 2);
        int authoredFights = authoredNodes.Count(id => contents[id].ContentType is 3 or 4);
        for (int index = 0; index < authoredNodes.Count; index++)
        {
            PunishaarStageContentTable content = contents[authoredNodes[index]];
            AssertEqual(content.Id, stage.CurrentNode.NodeId, $"Punishaar endless node {index + 1} id");
            switch (content.ContentType)
            {
                case 1:
                    if (stage.CurrentNode.Status == 7)
                    {
                        (List<PunishaarPush> shopPushes, XPunishaarSelectShopResponse selected) =
                            wire.Call<XPunishaarSelectShopRequest, XPunishaarSelectShopResponse>(nameof(XPunishaarSelectShopRequest),
                                new XPunishaarSelectShopRequest { ShopId = stage.CurrentNode.ShopInfo!.CandidateShopIds[0] });
                        AssertEqual(0, selected.Code, $"Punishaar endless shop {content.Id} code");
                        AssertEqual(0, shopPushes.Count, $"Punishaar endless shop {content.Id} pushes");
                        AssertEqual(2, selected.Node!.Status, $"Punishaar endless shop {content.Id} is processing");
                    }
                    break;
                case 2:
                    events++;
                    if (stage.CurrentNode.Status == 1)
                    {
                        (_, XPunishaarSelectEventResponse selected) = wire.Call<XPunishaarSelectEventRequest, XPunishaarSelectEventResponse>(
                            nameof(XPunishaarSelectEventRequest), new XPunishaarSelectEventRequest { EventId = stage.CurrentNode.EventInfo!.RandomEventIds[0] });
                        AssertEqual(0, selected.Code, $"Punishaar endless event {content.Id} select code");
                    }
                    (_, XPunishaarFinishEventResponse finished) = wire.Call<XPunishaarFinishEventRequest, XPunishaarFinishEventResponse>(
                        nameof(XPunishaarFinishEventRequest), new XPunishaarFinishEventRequest());
                    AssertEqual(0, finished.Code, $"Punishaar endless event {content.Id} finish code");
                    if (finished.Node!.Status != 4) break;
                    PunishaarRewardCardDetailInfo detail = RewardDetail(finished.Node.PendingRewardCardId, finished.Node.PendingRewardCardLevel);
                    (List<PunishaarPush> rewardPushes, XPunishaarHandlePendingRewardResponse handled) =
                        wire.Call<XPunishaarHandlePendingRewardRequest, XPunishaarHandlePendingRewardResponse>(
                            nameof(XPunishaarHandlePendingRewardRequest), new XPunishaarHandlePendingRewardRequest
                            {
                                IsAccept = true, CardDetail = detail
                            });
                    AssertEqual(0, handled.Code, $"Punishaar endless reward card {content.Id} code");
                    AssertEqual(5, handled.Node!.Status, $"Punishaar endless reward card {content.Id} finishes the node");
                    AssertEqual(true, rewardPushes.Any(push => push.Name is nameof(NotifyPunishaarMasterCardChange) or nameof(NotifyPunishaarSubCardChange)),
                        $"Punishaar endless reward card {content.Id} notifies the card");
                    break;
                case 3:
                case 4:
                    if (stage.CurrentNode.Status == 1)
                    {
                        (_, XPunishaarSelectFightResponse selected) = wire.Call<XPunishaarSelectFightRequest, XPunishaarSelectFightResponse>(
                            nameof(XPunishaarSelectFightRequest), new XPunishaarSelectFightRequest { FightId = stage.CurrentNode.FightInfo!.RandomFightIds[0] });
                        AssertEqual(0, selected.Code, $"Punishaar endless fight {content.Id} select code");
                    }
                    wire.Call<XPunishaarEnterFightRequest, XPunishaarEnterFightResponse>(nameof(XPunishaarEnterFightRequest), new XPunishaarEnterFightRequest());
                    (_, XPunishaarFinishFightResponse won) = wire.Call<XPunishaarFinishFightRequest, XPunishaarFinishFightResponse>(
                        nameof(XPunishaarFinishFightRequest), new XPunishaarFinishFightRequest
                        {
                            IsWin = true, LoseMaxSignalBallColor = 0, StartHp = 400, EndHp = 150, EnemyStartHp = 300, EnemyEndHp = 0
                        });
                    AssertEqual(0, won.Code, $"Punishaar endless fight {content.Id} code");
                    AssertEqual(5, won.Stage!.CurrentNode.Status, $"Punishaar endless fight {content.Id} finishes the node");
                    wins++;
                    break;
            }

            (_, XPunishaarExitNodeResponse exit) = wire.Call<XPunishaarExitNodeRequest, XPunishaarExitNodeResponse>(
                nameof(XPunishaarExitNodeRequest), new XPunishaarExitNodeRequest());
            AssertEqual(0, exit.Code, $"Punishaar endless exit node {content.Id} code");
            if (index + 1 < authoredNodes.Count)
            {
                stage = exit.Stage!;
                AssertEqual(1, stage.CurrentRound, $"Punishaar endless round stays open at node {content.Id}");
                continue;
            }
            // End of the round: the run restarts at the first node of the next round without settling.
            AssertEqual(true, exit.SettleInfo is null, "Punishaar endless round does not settle at the last node");
            stage = exit.Stage!;
            AssertEqual(2, stage.CurrentRound, "Punishaar endless round advances");
            AssertEqual(0, stage.HistoryNodeList.Count, "Punishaar endless round clears the history");
            AssertEqual(authoredNodes[0], stage.CurrentNode.NodeId, "Punishaar endless round restarts at the first node");
        }
        AssertEqual(authoredFights, wins, "Punishaar endless round fights every authored fight node");
        AssertEqual(authoredEvents, events, "Punishaar endless round finishes every authored event node");
        AssertEqual(authoredNodes.Count - authoredFights - authoredEvents, authoredNodes.Count(id => contents[id].ContentType == 1),
            "Punishaar endless round has shops only in the remaining nodes");
        AssertEqual(0, stage.HistoryNodeList.Count, "Punishaar endless second round starts with an empty history");

        (_, XPunishaarQuitStageResponse quit) = wire.Call<XPunishaarQuitStageRequest, XPunishaarQuitStageResponse>(
            nameof(XPunishaarQuitStageRequest), new XPunishaarQuitStageRequest());
        AssertEqual(2, quit.SettleInfo!.CurrentRound, "Punishaar endless quit reports the round");
        AssertEqual(true, quit.SettleInfo.IsNewRecord, "Punishaar endless quit sets the first record");
        Player durable = PunishaarDurable(players);
        AssertEqual(2, durable.Punishaar.BestRounds[endless.StageId], "Punishaar endless best round is durable");
        AssertEqual(wins, durable.Punishaar.BestWonNodeCounts[endless.StageId * 10 + 4],
            "Punishaar endless best fight-node count is durable");
        AssertEqual(0, durable.Punishaar.StageSaves.Count, "Punishaar endless quit removes the save");

        // One endless round is exactly the 7 choice-fight nodes task 120764 asks for, while 120765 needs
        // 14 (two rounds): the shared claim machinery must accept the first and still refuse the second.
        (_, FinishTaskResponse roundClaim) = wire.Call<FinishTaskRequest, FinishTaskResponse>(
            nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 120764 });
        AssertEqual(0, roundClaim.Code, "Punishaar endless fight task claim code");
        AssertEqual(true, roundClaim.RewardGoodsList.Count > 0, "Punishaar endless fight task pays the authored reward");
        AssertEqual(true, player.MissionProgress.ClaimedTaskIds.Contains(120764), "Punishaar endless fight task claim is recorded");
        (_, FinishTaskResponse twoRoundClaim) = wire.Call<FinishTaskRequest, FinishTaskResponse>(
            nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = 120765 });
        AssertEqual(true, twoRoundClaim.Code != 0, "Punishaar two-round fight task still cannot be claimed");
        AssertEqual(0, twoRoundClaim.RewardGoodsList.Count, "Punishaar two-round fight task pays nothing yet");
    }

    #endregion
}
