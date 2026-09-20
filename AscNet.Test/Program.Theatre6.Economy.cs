using AscNet.Common.Database;
using MessagePack;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using MongoDB.Bson;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.theatre6;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

// In-run economy and skill handling. Every assertion is a consumer-visible consequence of a
// registered request: gold/sanity deltas, sold/locked positions, ordered skill deltas and the
// authored overflow queue. Prices, slot limits and pool memberships come from the Theatre6 tables.
internal partial class Program
{
    // Authored shared reward shop (AscNet policy, no captured catalog): the harness funds the
    // account from the paired mission group's own RewardGoods, buys each authored preview bundle
    // once, and asserts the consumer-visible limits, closed-promotion boundary and journal.
    private static void ValidateRequiemRewardShopChecks()
    {
        using RequiemCase test = new("reward-shop");
        (_, _, int requiredLevel) = RequiemGate();
        test.Player.PlayerData.Level = requiredLevel;
        test.Reconcile(DateTimeOffset.UtcNow);

        List<Theatre6RewardTable> rewardTabs = TableReaderV2.Parse<Theatre6RewardTable>();
        List<Theatre6RewardTable> shops = rewardTabs
            .Where(row => row.ShopId is > 0).OrderBy(row => row.Priority).ToList();
        Require(shops.Count > 0, "No Theatre6 reward shop is authored.");
        List<uint> shopIds = shops.Select(row => (uint)Convert.ToInt32(row.ShopId)).ToList();
        JObject valid = test.Call("GetShopValidInfoRequest", Req(("IdList", shopIds.Select(id => (object?)id).ToList())));
        List<JObject> infos = (valid["ShopValidInfos"] as JArray ?? []).Children<JObject>().ToList();
        Dictionary<int, TaskTimeLimitTable> limits = TableReaderV2.Parse<TaskTimeLimitTable>().ToDictionary(row => row.Id);
        List<Theatre6RewardTable> missions = rewardTabs
            .Where(row => row.TaskTimeLimitId is > 0).OrderBy(row => row.Priority).ToList();
        AssertEqual(shops.Count, missions.Count, "Theatre6 reward shops have paired mission tabs");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index < shops.Count; index++)
        {
            int timeId = limits[missions[index].TaskTimeLimitId!.Value].TimeId ?? 0;
            bool expectedOpen = timeId == 0 || ActivityScheduleService.IsOpen(timeId, now);
            uint id = checked((uint)shops[index].ShopId!.Value);
            JObject info = infos.Single(entry => entry.Value<uint>("Id") == id);
            AssertEqual(!expectedOpen, info.Value<bool>("IsUnShelve"),
                $"Theatre6 reward shop {id} availability follows its paired mission window");
        }

        // Authored availability: a shop paired with a permanently open mission tab is promoted, a
        // shop whose paired tab owns a closed authored window is not. The client only sends valid
        // ids onward, so the catalog and purchase batches use exactly those.
        List<uint> validIds = infos.Where(info => !info.Value<bool>("IsUnShelve")).Select(info => info.Value<uint>("Id")).ToList();
        List<uint> closedIds = infos.Where(info => info.Value<bool>("IsUnShelve")).Select(info => info.Value<uint>("Id")).ToList();
        Require(validIds.Count > 0, "Theatre6 reward catalog must promote at least one authored shop.");
        // The client sends the promoted ids onward, and the catalog must never contain a shelved shop.
        JObject list = test.Call("GetFixedShopListRequest", Req(("IdList", validIds.Select(id => (object?)id).ToList())));
        List<JObject> clientShops = (list["ClientShopList"] as JArray ?? []).Children<JObject>().ToList();
        foreach (JObject shop in clientShops)
            Require(!closedIds.Contains(shop.Value<uint>("Id")), "Theatre6 reward catalog cannot publish a shelved shop.");

        JObject promoted = infos.First(info => !info.Value<bool>("IsUnShelve"));
        uint shopId = promoted.Value<uint>("Id");
        JObject listed = clientShops.FirstOrDefault(shop => shop.Value<uint>("Id") == shopId)
            ?? throw new InvalidDataException($"Theatre6 reward shop {shopId} is promoted but published no catalog.");
        List<JObject> goods = (listed["GoodsList"] as JArray ?? []).Children<JObject>().ToList();
        Require(goods.Count >= test.ShowItemCount(), "Theatre6 reward shop must publish one SKU per authored preview good.");
        JObject sku = goods.OrderBy(item => item.Value<uint?>("Id") ?? 0).First();
        uint goodsId = sku.Value<uint>("Id");

        int currency = test.FundRewardShop(shopId);
        long currencyBefore = test.Balance(currency);
        long unitPrice = test.RewardShopUnitPrice(shopId);
        Require(currencyBefore >= unitPrice, $"Theatre6 reward shop fixture could not fund the authored unit price {unitPrice}.");

        // Foreign SKU and unpromoted shop stay rejection-only and mutation-free.
        test.Reject("BuyRequest", Req(("ShopId", shopId), ("GoodsId", 999_999_999 + goodsId), ("Count", 1)),
            "Theatre6 reward purchase of a foreign SKU");
        if (closedIds.Count > 0)
        {
            uint closedShop = closedIds.First();
            test.Reject("BuyRequest", Req(("ShopId", closedShop), ("GoodsId", goodsId), ("Count", 1)),
                "Theatre6 reward purchase in a closed promotion");
        }

        JObject bought = test.Call("BuyRequest", Req(("ShopId", shopId), ("GoodsId", goodsId), ("Count", 1)));
        Require(bought["GoodList"] is JArray { Count: > 0 }, "Theatre6 reward purchase must grant the preview bundle.");
        int skuIndex = test.ShowItemIndex(sku["RewardGoods"]!.Value<int>("TemplateId"));
        long granted = bought["GoodList"]!.Children<JObject>()
            .Where(item => item.Value<int>("Id") == test.ShowItemId(skuIndex)
                || item.Value<int?>("TemplateId") == test.ShowItemId(skuIndex))
            .Sum(item => item.Value<long>("Count"));
        AssertEqual(Math.Max(1, unitPrice), currencyBefore - test.Balance(currency),
            "Theatre6 reward purchase charges the authored derived unit price");
        AssertEqual((long)test.ShowItemAmount(skuIndex), granted,
            "Theatre6 reward purchase grants the authored preview bundle count");
        test.Reject("BuyRequest", Req(("ShopId", shopId), ("GoodsId", goodsId), ("Count", 1)),
            "Theatre6 reward purchase of an already claimed bundle");
        // Persistence fault: a failed claim must neither grant nor charge, and the semantic retry
        // must then apply the purchase exactly once.
        JObject second = goods.OrderBy(item => item.Value<uint?>("Id") ?? 0).ElementAt(1);
        uint secondGoods = second.Value<uint>("Id");
        int secondItem = second["RewardGoods"]!.Value<int>("TemplateId");
        byte[] stateBefore = test.State.ToBson();
        long walletBefore = test.Balance(currency);
        test.Players.ThrowOnReplaceOne = true;
        try
        {
            test.Call("BuyRequest", Req(("ShopId", shopId), ("GoodsId", secondGoods), ("Count", 1)), success: false);
        }
        finally
        {
            test.Players.ThrowOnReplaceOne = false;
        }

        AssertEqual(Convert.ToHexString(stateBefore), Convert.ToHexString(test.State.ToBson()),
            "Failed Theatre6 reward purchase journal preserves durable state");
        AssertEqual(walletBefore, test.Balance(currency), "Failed Theatre6 reward purchase cannot charge the account");
        test.Call("BuyRequest", Req(("ShopId", shopId), ("GoodsId", secondGoods), ("Count", 1)));
        AssertEqual(walletBefore - Math.Max(1, unitPrice), test.Balance(currency),
            "Recovered Theatre6 reward purchase charges exactly once");
        Require((test.Session.inventory.Items.SingleOrDefault(item => item.Id == secondItem)?.Count ?? 0) >= 1,
            "Recovered Theatre6 reward purchase grants the authored bundle.");
        test.Reject("BuyRequest", Req(("ShopId", shopId), ("GoodsId", secondGoods), ("Count", 1)),
            "Recovered Theatre6 reward purchase cannot be claimed twice");

        JObject counted = test.Call("GetFixedShopListRequest", Req(("IdList", validIds.Select(id => (object?)id).ToList())));
        JObject countedSku = ((counted["ClientShopList"] as JArray ?? []).Children<JObject>()
                .First(shop => shop.Value<uint>("Id") == shopId)["GoodsList"] as JArray ?? [])
            .Children<JObject>().Single(item => item.Value<uint>("Id") == goodsId);
        AssertEqual(1, countedSku.Value<int>("TotalBuyTimes"),
            "Theatre6 reward purchase persists the per-account buy count");
        AssertEqual(1, countedSku.Value<int>("BuyTimesLimit"),
            "Theatre6 reward bundle is authored as a single per-account claim");
        long walletAfterPurchase = test.Balance(currency);
        test.Relog("reward-shop");
        JObject afterRelogCatalog = test.Call("GetFixedShopListRequest", Req(("IdList", validIds.Select(id => (object?)id).ToList())));
        JObject afterRelogSku = ((afterRelogCatalog["ClientShopList"] as JArray ?? []).Children<JObject>()
                .First(shop => shop.Value<uint>("Id") == shopId)["GoodsList"] as JArray ?? [])
            .Children<JObject>().Single(item => item.Value<uint>("Id") == goodsId);
        AssertEqual(1, afterRelogSku.Value<int>("TotalBuyTimes"),
            "Theatre6 reward purchase count survives relog");
        AssertEqual(walletAfterPurchase, test.Balance(currency),
            "Relog cannot refund or recharge a Theatre6 reward purchase");
        test.Reject("BuyRequest", Req(("ShopId", shopId), ("GoodsId", goodsId), ("Count", 1)),
            "Theatre6 reward purchase after relog with the persisted claim");
        AssertEqual(true, walletAfterPurchase > 0, "Theatre6 reward fixture kept its authored wallet after relog");
        Require(test.State.PendingMutation is null, "Recovered Theatre6 reward purchase leaves no journal.");
    }


    // Permanent Theatre6 missions (authored task tabs, permanent 707 first). Progress is proven
    // through the shared registered claim path with the authored task-domain conditions: an earned
    // mission claims and grants, a still-locked one is refused, a closed historical tab stays
    // unclaimable without disturbing the permanent counters, and the claimed state survives relog.
    private static void ValidateRequiemMissionProgressChecks()
    {
        using RequiemCase test = new("missions");
        test.UnlockGameplay();
        // Difficulty 1 is the tier the authored permanent missions progress on.
        (int character, int fashion, int buff, int difficulty) = RequiemBuild(1, 0);
        Require(test.MissionDifficulty(difficulty) is not null,
            "The authored permanent missions must cover the difficulty this fixture clears.");
        string modeKey = test.StartGameplayRun(character, fashion, buff, difficulty);
        test.WalkRun(modeKey, "missions");
        test.AcknowledgeRun(modeKey, modeId: 1, slot: 1);
        test.Reconcile(DateTimeOffset.UtcNow);
        Require(test.ArchiveCount() == 1, "Theatre6 mission fixture must archive exactly one cleared run.");
        Require(test.MissionProgress(difficulty) >= 1, "Clearing the authored difficulty must record mission progress.");

        // Consumer-visible choice counter: the public task snapshot must accumulate every completed
        // choice up to the authored target, and it must survive a relog.
        Require(test.CompletedChoices >= 2,
            "The authored run must complete at least two choices for the counter regression.");
        (int choiceMission, int choiceTarget) = test.ChoiceMission();
        int expectedProgress = Math.Min(test.CompletedChoices, choiceTarget);
        AssertEqual(expectedProgress, test.TaskScheduleValue(choiceMission),
            "Theatre6 mission counter accumulates every completed choice up to the authored target");

        (int earned, int locked) = test.MissionClaimCandidates();
        Require(earned > 0, "No permanent Theatre6 mission is earned by clearing the authored difficulty.");
        Require(locked > 0, "No permanent Theatre6 mission stays locked after one clear.");
        int closedTab = test.ClosedTabMission();
        long started = test.TotalInventoryCount();

        test.Reject("FinishTaskRequest", Req(("TaskId", closedTab)), "Theatre6 mission claim from a closed authored tab");
        test.Reject("FinishTaskRequest", Req(("TaskId", locked)), "Theatre6 mission claim before its authored condition");
        JObject claimed = test.Call("FinishTaskRequest", Req(("TaskId", earned)));
        Require(claimed["RewardGoodsList"] is JArray { Count: > 0 },
            "Claiming an earned Theatre6 mission must return its authored rewards.");
        Require(test.TotalInventoryCount() > started, "Claiming an earned Theatre6 mission must grant the authored rewards.");
        byte[] afterClaim = test.State.ToBson();
        long granted = test.TotalInventoryCount();
        int progressBefore = test.MissionProgress(difficulty);

        test.Relog("missions");
        AssertEqual(Convert.ToHexString(afterClaim), Convert.ToHexString(test.State.ToBson()),
            "Login keeps the claimed Theatre6 mission state and the surviving difficulty progress");
        test.Call("GetShopValidInfoRequest", Req(("IdList", new List<object?> { 1469 })));
        AssertEqual(expectedProgress, test.TaskScheduleValue(choiceMission),
            "Theatre6 mission counter survives relog instead of collapsing to the last increment");
        AssertEqual(progressBefore, test.MissionProgress(difficulty),
            "Refused and replayed claims cannot consume the permanent mission progress");
        test.Reject("FinishTaskRequest", Req(("TaskId", earned)), "Theatre6 mission claim replayed after relog");
        // The shared batch contract may idempotently succeed with no goods, so the assertion is that
        // a replayed batch claim cannot grant again, not that it must answer an error.
        long goodsBefore = test.InventoryFingerprint();
        test.Call("FinishMultiTaskRequest", Req(("TaskIds", new List<object?> { earned })), success: null);
        AssertEqual(goodsBefore, test.InventoryFingerprint(), "A replayed Theatre6 batch claim cannot grant again");

        AssertEqual(granted, test.TotalInventoryCount(), "Replayed Theatre6 mission claims cannot grant twice");
    }

    private static void ValidateRequiemShopAndSkillChecks()
    {
        using RequiemCase test = new("shop-skill");
        test.UnlockGameplay();
        // Pinned entropy for this fixture only: seed 95's natural run deals the authored unique
        // skill-upgrade relic (pack 100) at shop 10 through the unmodified pools, which is what makes
        // the buff-directed star-up request reachable without seeding items or altering the pools.
        test.Player.Theatre6.NextRunId = 95;
        (int character, int fashion, int buff, int difficulty) = RequiemBuild(0, 0);
        // The authored stage offers six shop rooms and the acquisition pools only fill slowly, so
        // the run is restarted while the shop/skill requests still lack a successful flow.
        for (int attempt = 0; attempt < 3 && !test.ShopSkillCovered(); attempt++)
        {
            string modeKey = test.StartGameplayRun(character, fashion, buff, difficulty);
            test.WalkRun(modeKey, $"shop-skill-{attempt}", confirmExtraFloor: false);
            test.AcknowledgeRun(modeKey, modeId: 1, slot: 0);
        }

        Require(test.ShopSkillCovered(), "Theatre6 shop/skill requests without a successful flow: " + string.Join(", ", test.MissingShopSkill()));
        AssertEqual(0, test.State.ActiveRuns.Count, "Acknowledged Theatre6 shop/skill runs are released");
        Require(test.ShopVisits >= 2, "Theatre6 shop/skill fixture must walk several authored shop rooms.");

        // Dedicated capped-offer regression. Seed 70's natural walk is the exact state that used to
        // fail: a relic reached its authored LimitCount while the offer pool still named it, so the
        // choice (and any purchase of it) answered Code 1. With the pool holding caps here, walking
        // that same seed to the first shop must complete and every offer it generated must respect the
        // authored caps, with an uncapped relic still claimable through the ordinary handler.
        using (RequiemCase capped = new("capped-offer"))
        {
            capped.UnlockGameplay();
            capped.Player.Theatre6.NextRunId = 70;
            (int cappedCharacter, int cappedFashion, int cappedBuff, int cappedDifficulty) = RequiemBuild(0, 0);
            string cappedMode = capped.StartGameplayRun(cappedCharacter, cappedFashion, cappedBuff, cappedDifficulty);
            capped.WalkToRoom(cappedMode, 3, "capped-offer");
            capped.AssertCappedOffersRespectCaps(cappedMode, "capped-offer");
        }
        string[] authoredDeltas =
        [
            "NotifyTheatre6GoldChange", "NotifyTheatre6SanChange", "NotifyTheatre6HealthChange",
            "NotifyTheatre6GoodsChange", "NotifyTheatre6AddBuff", "NotifyTheatre6BuffUpdate", "NotifyTheatre6DelBuff",
            "NotifyTheatre6AttrChange", "NotifyTheatre6AttrPackChange", "NotifyTheatre6TalentLevel",
            "Theatre6TotalScoreNotify", "NotifyTheatre6AddBgm", "NotifyTheatre6AddMessyCode"
        ];
        Require(test.ObservedPushes.Overlaps(authoredDeltas),
            "Theatre6 run must publish authored stat, buff or progression effects.");
        Console.WriteLine($"Theatre6 observed effects: {string.Join(", ", test.ObservedPushes.OrderBy(name => name))}");
    }

    private sealed partial class RequiemCase
    {
        private static readonly string[] RequiemShopSkillRequests =
        [
            "Theatre6ShopFreshRequest", "Theatre6ShopGoodLockRequest", "Theatre6ShopGoodBuyRequest",
            "Theatre6ShopGoodSellRequest", "Theatre6ShopBuySanRequest", "Theatre6EndShopRequest",
            "Theatre6SkillMoveOrSwapRequest", "Theatre6BuffLevelUpSkillRequest", "Theatre6SkillOverQueueSellRequest"
        ];

        public int ShopVisits { get; private set; }
        private bool assertedEmptyOverflow;
        private bool assertedOverflowBarrier;

        public bool ShopSkillCovered() => RequiemShopSkillRequests.All(CaseSuccessful.Contains);
        public IEnumerable<string> MissingShopSkill() => RequiemShopSkillRequests.Except(CaseSuccessful);

        public JObject ShopGood(string modeKey, int position) =>
            (Room(modeKey)["ShopGoods"] as JArray ?? []).Children<JObject>()
                .SingleOrDefault(goods => goods.Value<int>("Position") == position)
            ?? throw new InvalidDataException($"{Name}: the authored shop dealt no position {position}.");

        // Authoritative shop visit: every accepted action is chosen from the dealt goods and the
        // authored price tables, and the resulting deltas are asserted against the snapshot.
        public void ShopVisit(string modeKey, string label)
        {
            ShopVisits++;
            // Pinned-entropy guard: the authored first deal carries the unique skill-upgrade relic and a
            // paid refresh re-rolls unlocked positions, so claim it before refreshing. Everything else
            // (refresh, lock, further purchases, SAN, exit) keeps its existing order and coverage.
            int starUpPackId = RequiemConfigValue("SkillUpAttrPackId");
            JObject? starUpGood = DealtGoods(modeKey).FirstOrDefault(goods => goods.Value<int>("GoodId") == starUpPackId
                && !goods.Value<bool>("IsSell") && !goods.Value<bool>("IsLock")
                && RequiemGoodPrice(goods) >= 0 && Gold(modeKey) >= RequiemGoodPrice(goods));
            if (starUpGood is not null)
            {
                int starUpPrice = RequiemGoodPrice(starUpGood);
                int starUpGold = Gold(modeKey);
                JObject starUpBought = Call("Theatre6ShopGoodBuyRequest", Req(("Pos", starUpGood.Value<int>("Position"))));
                AssertEqual(starUpPackId, starUpBought.Value<int>("AttrPackId"),
                    "Theatre6 skill-upgrade relic purchase reports the authored relic pack");
                AssertEqual(starUpGold - starUpPrice, Gold(modeKey),
                    "Theatre6 skill-upgrade relic purchase charges the authored price");
            }

            JObject room = Room(modeKey);
            Theatre6StageShopTable shop = ShopRow(room.Value<int>("ShopId"));
            int expectedFresh = room.Value<int>("ShopFreshCount");
            int refreshPrice = Convert.ToInt32(shop.BaseRereshPrice) + (expectedFresh * Convert.ToInt32(shop.RefreshAddPrice));
            if (Gold(modeKey) >= refreshPrice)
            {
                int goldBefore = Gold(modeKey);
                JObject fresh = Call("Theatre6ShopFreshRequest", Req(("ShopFreshCount", expectedFresh)));
                AssertEqual(expectedFresh + 1, fresh.Value<int>("ShopFreshCount"),
                    "Theatre6 shop refresh advances the authored reroll counter");
                Require(fresh["ShopGoods"] is JArray { Count: > 0 }, "Theatre6 shop refresh must deal authored goods.");
                AssertEqual(goldBefore - refreshPrice, Gold(modeKey),
                    "Theatre6 shop refresh charges the authored reroll price");
                Require(Pushes.Any(push => push.Name == "NotifyTheatre6GoldChange"),
                    "Theatre6 shop refresh reports the gold change to the client.");
            }

            if (DealtGoods(modeKey).Any(goods => !goods.Value<bool>("IsSell")))
            {
                // The installed client sends the CURRENT lock state and applies the response, so the
                // server toggles and rejects a stale value without changing anything.
                int position = LockablePosition(modeKey);
                bool current = ShopGood(modeKey, position).Value<bool>("IsLock");
                JObject locked = Call("Theatre6ShopGoodLockRequest", Req(("Pos", position), ("IsLock", current)));
                AssertEqual(!current, locked.Value<bool>("IsLock"), "Theatre6 shop lock toggles the requested current state");
                AssertEqual(!current, ShopGood(modeKey, position).Value<bool>("IsLock"),
                    "Theatre6 toggled lock persists in the room snapshot");
                AssertStaleLockRejected(modeKey, position, label);
                JObject unlocked = Call("Theatre6ShopGoodLockRequest", Req(("Pos", position), ("IsLock", !current)));
                AssertEqual(current, unlocked.Value<bool>("IsLock"), "Theatre6 shop unlock toggles the flipped state back");
                AssertEqual(current, ShopGood(modeKey, position).Value<bool>("IsLock"),
                    "Theatre6 unlocked position persists in the room snapshot");
                // A duplicate transport id replays the frozen answer instead of toggling again.
                byte[] toggled = State.ToBson();
                JObject replayLock = Call("Theatre6ShopGoodLockRequest", Req(("Pos", position), ("IsLock", !current)),
                    reusePacketId: LastPacketId);
                AssertEqual(current, replayLock.Value<bool>("IsLock"), "Duplicate Theatre6 lock returns the frozen answer");
                AssertEqual(Convert.ToHexString(toggled), Convert.ToHexString(State.ToBson()),
                    "Duplicate Theatre6 lock cannot toggle the good twice");
                if (!current)
                {
                    Call("Theatre6ShopGoodLockRequest", Req(("Pos", position), ("IsLock", false)));
                    AssertLockedSurvivesRefresh(modeKey, position);
                }
            }

            // Buy the dealt goods that open the remaining coverage: the authored skill-upgrade relic
            // first when the shop deals it (Config SkillUpAttrPackId, whose buff 53 is what the
            // buff-directed upgrade request needs), then the best affordable dealt skill so the sale
            // path is exercised from a naturally purchased skill. Both are ordinary purchases of
            // naturally dealt, unlocked, affordable positions - nothing is seeded.
            List<JObject> affordable = DealtGoods(modeKey)
                .Where(goods => !goods.Value<bool>("IsSell") && !goods.Value<bool>("IsLock")
                    && RequiemGoodPrice(goods) >= 0 && Gold(modeKey) >= RequiemGoodPrice(goods))
                .ToList();
            foreach (int wantedType in new[] { 2, 1 })
            {
                JObject? purchase = affordable
                    .Where(goods => goods.Value<int>("Type") == wantedType
                        && !ShopGood(modeKey, goods.Value<int>("Position")).Value<bool>("IsSell")
                        && Gold(modeKey) >= RequiemGoodPrice(ShopGood(modeKey, goods.Value<int>("Position"))))
                    .OrderByDescending(goods => goods.Value<int>("GoodId") == starUpPackId)
                    .ThenBy(goods => RequiemGoodPrice(goods))
                    .FirstOrDefault();
                if (purchase is null)
                    continue;
                int position = purchase.Value<int>("Position");
                int goodId = purchase.Value<int>("GoodId");
                int type = purchase.Value<int>("Type");
                int price = RequiemGoodPrice(ShopGood(modeKey, position));
                int goldBefore = Gold(modeKey);
                long inventoryBefore = InventoryFingerprint();
                // The authored placement predicate only describes a brand-new family member, and only
                // the state before the purchase can say which slot is free; a same-level copy instead
                // merges in place (2048), so its position is traced before/after rather than predicted.
                bool alreadyOwned = type == 1 && SkillSlotType(modeKey, goodId) > 0;
                bool familyEquipped = type == 1 && (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                    .Any(skill => skill.Value<int>("SkillId") > 0 && skill.Value<int>("SlotType") != 4
                        && SkillFamily(skill.Value<int>("SkillId")) == SkillFamily(goodId));
                // A brand-new family member follows the authored placement priority; when the family is
                // already equipped the runtime's family rule sends the arriving copy to the bag, so that
                // is the expected slot instead of the raw priority list.
                int expectedSlot = type == 1 && !alreadyOwned
                    ? (familyEquipped ? 4 : ExpectedAcquisitionSlot(modeKey, goodId))
                    : 0;
                // Positions and copy count of that family BEFORE the purchase: a same-level merge has to
                // upgrade one of those existing copies in place, whichever copy the runtime picks.
                HashSet<(int Slot, int Position)> familyBefore = type == 1
                    ? (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                        .Where(skill => skill.Value<int>("SkillId") > 0
                            && SkillFamily(skill.Value<int>("SkillId")) == SkillFamily(goodId))
                        .Select(skill => (skill.Value<int>("SlotType"), skill.Value<int>("Position"))).ToHashSet()
                    : [];
                int familyCopiesBefore = type == 1 ? OwnedFamilyCopies(modeKey, goodId) : 0;
                int previousLevel = type == 1 && alreadyOwned ? AuthoredSkillLevel(goodId) : 0;
                JObject bought = Call("Theatre6ShopGoodBuyRequest", Req(("Pos", position)));
                JObject sold = bought["SellShopGood"] as JObject
                    ?? throw new InvalidDataException($"{label}: Theatre6 purchase returned no SellShopGood.");
                AssertEqual(position, sold.Value<int>("Position"), "Theatre6 purchase acknowledges the requested position");
                AssertEqual(goodId, sold.Value<int>("GoodId"), "Theatre6 purchase acknowledges the dealt good");
                AssertEqual(1, sold.Value<int>("IsSell"), "Theatre6 purchase marks the position sold");
                AssertEqual(type, sold.Value<int>("Type"), "Theatre6 purchase reports the authored good type");
                AssertEqual(goldBefore - price, Gold(modeKey), "Theatre6 shop purchase charges the authored price");
                AssertEqual(inventoryBefore, InventoryFingerprint(), "Theatre6 in-run purchase cannot mint account inventory");
                if (type == 1)
                {
                    Require(bought["SkillUpdates"] is JArray { Count: > 0 }, "Theatre6 skill purchase must return the skill delta.");
                    JArray updates = (JArray)bought["SkillUpdates"]!;
                    int gained = FirstAddedSkill(updates.Children<JObject>());
                    int queued = updates.Children<JObject>()
                        .Select(update => update.Value<int>("FullEnQueueSkill")).FirstOrDefault(id => id > 0);
                    // An upgrade replaces the stored entry with the AUTHORED next-level id, so the family
                    // is what stays observable; the old id is gone from the snapshot by design.
                    int upgraded = updates.Children<JObject>()
                        .SelectMany(update => (update["ReplaceSkills"] as JArray ?? []).Children<JObject>())
                        .Select(entry => entry.Value<int>("SkillId"))
                        .Where(id => id > 0 && SkillFamily(id) == SkillFamily(goodId))
                        .DefaultIfEmpty(0).Max();
                    int sellTarget;
                    if (gained > 0)
                    {
                        // Brand-new family member: the authored placement predicate decided before the
                        // purchase is the observable outcome.
                        AssertEqual(true, SkillOwned(modeKey, gained), "Theatre6 purchased skill lands in the run skill list");
                        if (expectedSlot > 0)
                            AssertEqual(expectedSlot, SkillSlotType(modeKey, gained),
                                "Theatre6 acquired skill follows the authored slot priority, including the bag");
                        sellTarget = gained;
                    }
                    else if (queued == goodId)
                    {
                        // Every authored placement target (Special 1, Active 4, Insert 3, Bag 8) is full:
                        // the acquisition is queued and the client resolves it through the sell popup.
                        Require(((Mode(modeKey)["SkillOverQueue"] as JArray) ?? []).Values<int>().Contains(goodId),
                            $"{label}: Theatre6 overflow purchase must enqueue the purchased skill.");
                        sellTarget = SellableOwnedSkill(modeKey, 0);
                    }
                    else
                    {
                        // Same-family, same-level copy: the 2048 merge upgrades the stored entry in place
                        // to the authored next level (the shop's own upgrade-arrow behaviour).
                        Require(upgraded > 0 && AuthoredSkillLevel(upgraded) > previousLevel,
                            $"{label}: Theatre6 skill purchase must add, upgrade or queue the skill.");
                        Require(familyBefore.Contains((SkillSlotType(modeKey, upgraded), OwnedPosition(modeKey, upgraded))),
                            $"{label}: a same-level Theatre6 merge must upgrade an existing family copy in place");
                        AssertEqual(familyCopiesBefore, OwnedFamilyCopies(modeKey, goodId),
                            $"{label}: a same-level Theatre6 merge consumes the purchased copy into the existing one");
                        sellTarget = upgraded;
                    }

                    if (sellTarget > 0)
                    {
                        // The client sells an owned skill by dragging it inside the shop; the sale pays
                        // the authored SellPrice and consumes exactly one owned copy at its slot. A
                        // queued or stored copy of the same id legitimately survives, so the assertion
                        // is per-copy consumption, never global id absence.
                        int sellPrice = Convert.ToInt32(SkillRow(sellTarget).SellPrice);
                        int goldBeforeSell = Gold(modeKey);
                        int copiesBefore = OwnedCopies(modeKey, sellTarget);
                        int soldSlot = SkillSlotType(modeKey, sellTarget);
                        int soldPosition = OwnedPosition(modeKey, sellTarget);
                        JObject sale = Call("Theatre6ShopGoodSellRequest", Req(("SkillId", sellTarget)));
                        Require(sale["SkillUpdates"] is JArray { Count: > 0 }, "Theatre6 skill sale must return the removal delta.");
                        JObject? removal = sale["SkillUpdates"]!.Children<JObject>()
                            .SelectMany(update => update["RemovesSkills"] is JArray removals
                                ? removals.Children<JObject>()
                                : Enumerable.Empty<JObject>())
                            .FirstOrDefault(entry => entry.Value<int>("SkillId") == sellTarget);
                        Require(removal is not null, "Theatre6 skill sale delta must remove the sold skill");
                        AssertEqual(soldSlot, removal!.Value<int>("SlotType"), "Theatre6 skill sale removes the sold copy's slot");
                        AssertEqual(soldPosition, removal.Value<int>("Position"), "Theatre6 skill sale removes the sold copy's position");
                        AssertEqual(goldBeforeSell + sellPrice, Gold(modeKey), "Theatre6 skill sale pays the authored sell price");
                        AssertEqual(copiesBefore - 1, OwnedCopies(modeKey, sellTarget),
                            "Theatre6 skill sale consumes exactly one owned copy of the sold skill");
                    }
                }
                else
                {
                    Require(bought["AttrPackId"] is not null, "Theatre6 relic purchase must report the granted relic pack.");
                    int packId = bought.Value<int>("AttrPackId");
                    AssertEqual(true, AttrPackCount(modeKey, packId) > 0, "Theatre6 relic purchase lands in the run relic map");
                    AssertEqual(true, TableReaderV2.Parse<Theatre6AttrPackTable>().Any(row => Convert.ToInt32(row.Id) == packId),
                        "Theatre6 relic purchase reports an authored relic pack");
                    AssertRelicAcquisitionEffects(modeKey, packId);
                }
            }

            // Sanity purchase: only offered while the authored price is affordable and sanity is
            // below its cap, and the counter is the expected persisted value.
            JObject sanRoom = Room(modeKey);
            int buySanTimes = sanRoom.Value<int>("BuySanTimes");
            int sanPrice = Convert.ToInt32(shop.BuySanBasePrice) + (buySanTimes * Convert.ToInt32(shop.BuySanAddPrice));
            if (Gold(modeKey) >= sanPrice && Mode(modeKey).Value<int>("San") < Mode(modeKey).Value<int>("MaxSan"))
            {
                int sanBefore = Mode(modeKey).Value<int>("San");
                int maxSan = Mode(modeKey).Value<int>("MaxSan");
                int goldBeforeSan = Gold(modeKey);
                JObject san = Call("Theatre6ShopBuySanRequest", Req(("BuySanTimes", buySanTimes)));
                AssertEqual(buySanTimes + 1, san.Value<int>("BuySanTimes"), "Theatre6 sanity purchase advances the authored counter");
                AssertEqual(goldBeforeSan - sanPrice, Gold(modeKey), "Theatre6 sanity purchase charges the authored price curve");
                AssertEqual(Math.Min(maxSan, sanBefore + Convert.ToInt32(shop.BuySanNum)), Mode(modeKey).Value<int>("San"),
                    "Theatre6 sanity purchase restores the authored amount within the cap");
                Require(Pushes.Any(push => push.Name == "NotifyTheatre6SanChange"),
                    "Theatre6 sanity purchase reports the sanity change to the client.");
                Require(Pushes.Any(push => push.Name == "NotifyTheatre6GoldChange"),
                    "Theatre6 sanity purchase reports the gold change to the client.");
            }

            ResolveSkillRequests(modeKey, label);
            string roomBeforeExit = RoomIdentity(modeKey);
            Call("Theatre6EndShopRequest", null);
            AssertEqual(true, RoomIdentity(modeKey) != roomBeforeExit || Mode(modeKey).Value<bool>("IsSettle"),
                "Leaving the Theatre6 shop advances the authored chain exactly at the exit");
        }

        // Skills are managed from the shop room, exactly where the client's battle-shop UI does it.
        private void ResolveSkillRequests(string modeKey, string label)
        {
            // The authored star-up chance is a live buff with StageBuff effect 12 (buff 53, granted by
            // the skill-upgrade relic). It is read from the run snapshot rather than from the granting
            // response, so the buff-directed upgrade is attempted whenever the natural path produced
            // the chance - including a shop purchase earlier in this same visit.
            JObject? starUp = LiveBuffs(modeKey)
                .FirstOrDefault(buff => BuffRow(buff.Value<int>("BuffId")).BuffEffectType == 12
                    && buff.Value<int>("TriggerCount") < StarUpChances(buff.Value<int>("BuffId")));
            if (starUp is not null)
            {
                int upgradeTarget = UpgradableOwnedSkill(modeKey, starUp.Value<int>("BuffId"));
                if (upgradeTarget > 0)
                {
                    int buffUid = starUp.Value<int>("Uid");
                    int levelBefore = AuthoredSkillLevel(upgradeTarget);
                    JObject upgraded = Call("Theatre6BuffLevelUpSkillRequest", Req(("BuffId", buffUid), ("SkillId", upgradeTarget)));
                    Require(upgraded["SkillUpdates"] is JArray { Count: > 0 },
                        $"{label}: Theatre6 skill upgrade must return the skill delta.");
                    int upgradedId = upgraded["SkillUpdates"]!.Children<JObject>()
                        .SelectMany(update => (update["ReplaceSkills"] as JArray ?? []).Children<JObject>())
                        .Where(entry => entry is not null)
                        .Select(entry => entry.Value<int>("SkillId"))
                        .Where(id => id > 0 && TableReaderV2.Parse<Theatre6SkillTable>()
                            .Any(row => Convert.ToInt32(row.Id) == id && Convert.ToInt32(row.SkillKey) == SkillFamily(upgradeTarget)))
                        .DefaultIfEmpty(0).Max();
                    Require(upgradedId > 0 && AuthoredSkillLevel(upgradedId) > levelBefore,
                        $"{label}: Theatre6 buff-directed upgrade must raise the authored skill level.");
                    Require(SkillOwned(modeKey, upgradedId),
                        $"{label}: Theatre6 buff-directed upgrade keeps the upgraded skill owned.");
                }
            }

            // Authored overflow is reached only when every placement target (Special 1, Active 4,
            // Insert 3, Bag 8) is full and an acquisition still arrives; when a bounded run cannot
            // fill the board the authored empty-queue rejection is asserted instead.
            // SkillOverQueue is the authoritative integer skill-id list the client reads.
            List<int> overflowQueue = (Mode(modeKey)["SkillOverQueue"] as JArray ?? []).Values<int>().ToList();
            bool overflow = overflowQueue.Count > 0;
            if (!overflow && !assertedEmptyOverflow)
            {
                // The authored empty-queue rejection is verified once while the board is still open,
                // so the request's failure contract is covered even when the pools never overflow.
                assertedEmptyOverflow = true;
                JObject empty = Call("Theatre6SkillOverQueueSellRequest", null, false);
                AssertEqual(20423084, empty.Value<int>("Code"), "Theatre6 empty overflow queue rejects with the authored code");
            }

            if (overflow)
            {
                // The unresolved overflow queue is a trust boundary: leaving the shop must be refused
                // with the authored barrier code and must not advance the authored chain.
                if (!assertedOverflowBarrier)
                {
                    assertedOverflowBarrier = true;
                    string roomBeforeBarrier = RoomIdentity(modeKey);
                    JObject barrier = Call("Theatre6EndShopRequest", null, false);
                    AssertEqual(20423085, barrier.Value<int>("Code"),
                        "Unresolved Theatre6 overflow blocks leaving the shop with the authored code");
                    AssertEqual(roomBeforeBarrier, RoomIdentity(modeKey),
                        "Blocked Theatre6 shop exit cannot advance the room");
                }

                int queuedValue = overflowQueue.Sum(queuedSkill => Convert.ToInt32(TableReaderV2.Parse<Theatre6SkillTable>()
                    .Single(row => Convert.ToInt32(row.Id) == queuedSkill).SellPrice));
                int goldBefore = Gold(modeKey);
                Call("Theatre6SkillOverQueueSellRequest", null);
                AssertEqual(0, (Mode(modeKey)["SkillOverQueue"] as JArray)?.Count ?? 0,
                    "Theatre6 overflow sale consumes the whole queue");
                Require(overflowQueue.All(queuedSkill => TableReaderV2.Parse<Theatre6SkillTable>()
                        .Any(row => Convert.ToInt32(row.Id) == queuedSkill)),
                    "Theatre6 overflow queue must hold authored skill ids");
                AssertEqual(goldBefore + queuedValue, Gold(modeKey), "Theatre6 overflow sale pays the authored skill value");
            }

            AssertEquippedFamilyUniqueness(modeKey);

            JObject? mover = OwnedSkillForMove(modeKey);
            if (mover is null)
                return;
            int sourceId = mover.Value<int>("SkillId");
            int sourceSlotType = mover.Value<int>("SlotType");
            int sourcePosition = mover.Value<int>("Position");
            // Destinations are authored per skill family (the config sort lists are the client's own
            // eligibility source): prefer a free authored slot, otherwise swap with an occupant of an
            // authored slot that holds a different family, so move and swap coverage both stay.
            (int dstSlot, int dstPosition) = AuthoredDestination(modeKey, sourceId, sourceSlotType, sourcePosition);
            if (dstSlot == 0)
                return;
            JObject moved = Call("Theatre6SkillMoveOrSwapRequest",
                Req(("SrcSkillId", sourceId), ("DstSlotType", dstSlot), ("DstPosition", dstPosition)));
            Require(moved["SkillUpdates"] is JArray { Count: > 0 }, $"{label}: Theatre6 skill move must return the ordered delta.");
            AssertEqual(true, SkillInstalled(modeKey, sourceId, dstSlot, dstPosition),
                "Theatre6 skill move installs the requested skill at the requested slot");
            // A move leaves the source empty; a swap legitimately seats the displaced skill there, so
            // only duplication of the moved skill (or losing it) is an error.
            int sourceOccupant = SkillAt(modeKey, sourceSlotType, sourcePosition);
            Require(sourceOccupant != sourceId,
                "Theatre6 skill move must not leave the moved skill duplicated in its source slot");
            AssertEqual(true, SkillOwned(modeKey, sourceId),
                "Theatre6 skill move keeps the moved skill owned");

            // Swap coverage on the same predicate: with the skill now in the Bag (a source that the
            // runtime exempts from the displaced-skill installability rule), swap it onto an occupied
            // equip slot of a different family that is in its authored install slots.
            if (dstSlot == 4)
            {
                (int swapSlot, int swapPosition) = AuthoredSwapDestination(modeKey, sourceId);
                if (swapSlot != 0)
                {
                    Require(!HasOtherEquippedFamily(modeKey, sourceId),
                        $"{label}: Theatre6 swap fixture must not request an equipped-family conflict " +
                        $"(equipped=[{EquippedSkillTrace(modeKey)}])");
                    int displaced = SkillAt(modeKey, swapSlot, swapPosition);
                    JObject swapped = Call("Theatre6SkillMoveOrSwapRequest", Req(("SrcSkillId", sourceId),
                        ("DstSlotType", swapSlot), ("DstPosition", swapPosition)));
                    Require(swapped["SkillUpdates"] is JArray { Count: > 0 }, $"{label}: Theatre6 skill swap must return the ordered delta.");
                    AssertEqual(true, SkillInstalled(modeKey, sourceId, swapSlot, swapPosition),
                        "Theatre6 skill swap installs the moved skill on the occupied slot");
                    AssertEqual(true, SkillOwned(modeKey, displaced),
                        "Theatre6 skill swap keeps the displaced skill owned");
                }
            }

            // A swap with a peer in the mover's CURRENT slot must also be accepted (the client's own
            // swap path). The mover already moved once above, so its slot is re-read from a fresh
            // snapshot; the peer must satisfy the client's swap predicate for that slot — different
            // family, and when the shared slot is an equip slot the peer has to be installable back
            // into it (XTheatre6ControlCharacter:331-347), otherwise the request is not one a client
            // would ever issue and the runtime correctly refuses it.
            int moverFamily = SkillFamily(sourceId);
            int currentSlot = SkillSlotType(modeKey, sourceId);
            int currentPosition = SkillPositionOf(modeKey, sourceId);
            JObject? peer = (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                .FirstOrDefault(skill => skill.Value<int>("SkillId") != sourceId
                    && skill.Value<int>("SlotType") == currentSlot
                    && skill.Value<int>("Position") != currentPosition
                    && SkillFamily(skill.Value<int>("SkillId")) != moverFamily
                    && (currentSlot == 4
                        || AuthoredSlotTypes(modeKey, skill.Value<int>("SkillId")).Contains(currentSlot)));
            if (peer is not null)
            {
                int peerId = peer.Value<int>("SkillId");
                JObject swapped = Call("Theatre6SkillMoveOrSwapRequest", Req(("SrcSkillId", sourceId),
                    ("DstSlotType", currentSlot), ("DstPosition", peer.Value<int>("Position"))));
                Require(swapped["SkillUpdates"] is JArray { Count: > 0 }, $"{label}: Theatre6 skill swap must return the ordered delta.");
                AssertEqual(true, SkillOwned(modeKey, peerId), "Theatre6 skill swap keeps the displaced skill owned");
            }
        }

        private int SkillPositionOf(string modeKey, int skillId) => (Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>().Where(skill => skill.Value<int>("SkillId") == skillId)
            .Select(skill => skill.Value<int>("Position")).DefaultIfEmpty(0).First();


        // The authored mission tab a reward shop pairs with: no TimeId means permanently open,
        // otherwise the tab follows that promoted activity's own window.
        public bool RewardShopTabOpen(int timeLimitId) => timeLimitId <= 0 || MissionsOpen(timeLimitId);

        // Authored slot priority per skill type (config ActiveSkillSlotSort/ClashSkillSlotSort/
        // UltraCalcSkillSlotSort/InsertSkillSlotSort); the bag is the last authored fallback.
        public int ExpectedAcquisitionSlot(string modeKey, int skillId)
        {
            Theatre6SkillTable skill = TableReaderV2.Parse<Theatre6SkillTable>()
                .Single(row => Convert.ToInt32(row.Id) == skillId);
            string key = Convert.ToInt32(skill.Type) switch
            {
                1 => "ActiveSkillSlotSort",
                2 => "ClashSkillSlotSort",
                3 => "UltraCalcSkillSlotSort",
                4 => "InsertSkillSlotSort",
                _ => "ActiveSkillSlotSort"
            };
            foreach (int slotType in TableReaderV2.Parse<Theatre6ConfigTable>().Single(row => row.Key == key).Values)
                if (FreeSlotPosition(modeKey, slotType) > 0)
                    return slotType;
            // The bag is the authored last resort for an acquired skill.
            return 4;
        }

        public int SkillSlotType(string modeKey, int skillId) => (Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>().Where(skill => skill.Value<int>("SkillId") == skillId)
            .Select(skill => skill.Value<int>("SlotType")).DefaultIfEmpty(0).First();

        // A bought relic must run its authored acquisition effects: the attr gains it carries and
        // the live buffs it grants, both visible in the run snapshot.
        public void AssertRelicAcquisitionEffects(string modeKey, int packId)
        {
            Theatre6AttrPackTable pack = TableReaderV2.Parse<Theatre6AttrPackTable>()
                .Single(row => Convert.ToInt32(row.Id) == packId);
            for (int index = 0; index < pack.AttrTypes.Count; index++)
            {
                int attrId = pack.AttrTypes[index];
                if (attrId <= 0) continue;
                int gain = index < pack.AttrNums.Count ? pack.AttrNums[index] : 0;
                if (gain <= 0) continue;
                AssertEqual(true, AttrValue(modeKey, attrId) > 0 || gain == 0,
                    $"Theatre6 relic {packId} must apply its authored attribute {attrId}");
            }

            int grantedBuff = Convert.ToInt32(pack.BuffIds ?? 0);
            if (grantedBuff > 0)
                Require((Mode(modeKey)["Buffs"] as JObject ?? []).Properties()
                        .Any(property => property.Value.Value<int>("BuffId") == grantedBuff)
                        || Pushes.Any(push => push.Name is "NotifyTheatre6AddBuff" or "NotifyTheatre6BuffUpdate"),
                    $"Theatre6 relic {packId} must apply its authored buff {grantedBuff}");
        }

        public int AttrValue(string modeKey, int attrId) => (Mode(modeKey)["Attrs"] as JArray ?? [])
            .Children<JObject>().Where(attr => attr.Value<int>("AttrId") == attrId)
            .Select(attr => attr.Value<int>("Value")).DefaultIfEmpty(0).Max();

        // Locked stock is player state: a reroll must not drop or unlock it.
        public void AssertLockedSurvivesRefresh(string modeKey, int position)
        {
            JObject locked = ShopGood(modeKey, position);
            int goodId = locked.Value<int>("GoodId");
            int expectedFresh = Room(modeKey).Value<int>("ShopFreshCount");
            int refreshPrice = RequiemShopPrice(modeKey);
            if (Gold(modeKey) < refreshPrice)
                return;
            JObject fresh = Call("Theatre6ShopFreshRequest", Req(("ShopFreshCount", expectedFresh)));
            Require(fresh["ShopGoods"] is JArray { Count: > 0 }, "Theatre6 shop reroll must deal authored goods.");
            JObject after = ShopGood(modeKey, position);
            AssertEqual(true, after.Value<bool>("IsLock"), "Theatre6 shop reroll keeps the locked position locked");
            AssertEqual(goodId, after.Value<int>("GoodId"), "Theatre6 shop reroll keeps the locked stock");
            Call("Theatre6ShopGoodLockRequest", Req(("Pos", position), ("IsLock", true)));
        }

        // Authored family rule: at most one equipped skill per family, whatever its level.
        public void AssertEquippedFamilyUniqueness(string modeKey)
        {
            List<JObject> equipped = (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                .Where(skill => skill.Value<int>("SlotType") != 4)
                .ToList();
            List<int> families = equipped
                .Select(skill => Convert.ToInt32(TableReaderV2.Parse<Theatre6SkillTable>()
                    .Single(row => Convert.ToInt32(row.Id) == skill.Value<int>("SkillId")).SkillKey))
                .ToList();
            AssertEqual(families.Count, families.Distinct().Count(),
                "Theatre6 equips at most one skill per authored family, independent of level");
        }

        // A stale lock value is refused without changing the good.
        public void AssertStaleLockRejected(string modeKey, int position, string label)
        {
            bool current = ShopGood(modeKey, position).Value<bool>("IsLock");
            byte[] before = State.ToBson();
            JObject response = Call("Theatre6ShopGoodLockRequest", Req(("Pos", position), ("IsLock", !current)), false);
            AssertEqual(true, response.Value<int>("Code") != 0, $"{label}: stale Theatre6 lock value must be rejected");
            AssertEqual(Convert.ToHexString(before), Convert.ToHexString(State.ToBson()),
                $"{label}: stale Theatre6 lock value cannot change the good");
        }

        public int RequiemShopPrice(string modeKey)
        {
            JObject room = Room(modeKey);
            Theatre6StageShopTable shop = ShopRow(room.Value<int>("ShopId"));
            return Convert.ToInt32(shop.BaseRereshPrice) + (room.Value<int>("ShopFreshCount") * Convert.ToInt32(shop.RefreshAddPrice));
        }


        // Authored Theatre6 missions join through the TASK-domain condition table that Core's meta
        // tasks use (AscNet.Table.V2.share.task.ConditionTable), never the global share/condition
        // archive gates. Only the two domains this harness can derive from durable state are
        // evaluated (136009 difficulty clears, 136001 archived character score), so the loop is
        // never vacuous and never guesses a counter the tables do not expose to the client.
        public (int Earned, int Locked) MissionClaimCandidates()
        {
            Dictionary<int, AscNet.Table.V2.share.task.ConditionTable> conditions =
                TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>().ToDictionary(row => row.Id);
            List<int> earned = [];
            List<int> locked = [];
            foreach (Theatre6RewardTable tab in TableReaderV2.Parse<Theatre6RewardTable>()
                .Where(row => Convert.ToInt32(row.TaskTimeLimitId ?? 0) > 0)
                .OrderBy(row => Convert.ToInt32(row.Id)))
            {
                if (tab.TaskTimeLimitId is not { } limitId) continue;
                foreach (int taskId in PermanentMissions(limitId))
                {
                    TaskTable? task = TableReaderV2.Parse<TaskTable>().FirstOrDefault(row => Convert.ToInt32(row.Id) == taskId);
                    if (task is null) continue;
                    int conditionId = Convert.ToInt32(task.Condition);
                    if (conditionId <= 0) continue;
                    if (!conditions.TryGetValue(conditionId, out AscNet.Table.V2.share.task.ConditionTable? condition))
                        continue;
                    if (condition.Params.Count < 2) continue;
                    bool? satisfied = Convert.ToInt32(condition.Type) switch
                    {
                        136009 => State.PassDiffRecords.GetValueOrDefault(Convert.ToInt32(condition.Params[0])) >= Convert.ToInt32(condition.Params[1]),
                        136001 => State.Files.Any(file => file.CharacterId == Convert.ToInt32(condition.Params[0])
                            && file.Score >= Convert.ToInt32(condition.Params[1])),
                        _ => null
                    };
                    if (satisfied is null) continue;
                    (satisfied.Value ? earned : locked).Add(taskId);
                }
            }

            return (earned.FirstOrDefault(0), locked.FirstOrDefault(0));
        }

        public List<int> PermanentMissions(int timeLimitId)
        {
            TaskTimeLimitTable? limit = TableReaderV2.Parse<TaskTimeLimitTable>()
                .FirstOrDefault(row => Convert.ToInt32(row.Id) == timeLimitId);
            return limit is null ? [] : limit.TaskId.Where(id => id > 0).ToList();
        }

        // The historical tab whose authored window has already closed: its missions must stay
        // unclaimable, and refusing them must not disturb the permanent tab's counters.
        public int ClosedTabMission()
        {
            foreach (Theatre6RewardTable tab in TableReaderV2.Parse<Theatre6RewardTable>()
                .Where(row => Convert.ToInt32(row.TaskTimeLimitId ?? 0) > 0)
                .OrderBy(row => Convert.ToInt32(row.Id)))
            {
                if (tab.TaskTimeLimitId is not { } limitId) continue;
                List<int> missions = PermanentMissions(limitId);
                if (missions.Count == 0) continue;
                if (MissionsOpen(limitId)) continue;
                return missions[0];
            }

            throw new InvalidDataException($"{Name}: no authored Theatre6 mission tab is currently closed.");
        }

        // The authored tab window: a TimeId with no schedule entry is permanent, otherwise the
        // tab follows that promoted activity's own window.
        public bool MissionsOpen(int timeLimitId)
        {
            TaskTimeLimitTable limit = TableReaderV2.Parse<TaskTimeLimitTable>()
                .Single(row => Convert.ToInt32(row.Id) == timeLimitId);
            int timeId = Convert.ToInt32(limit.TimeId ?? 0);
            return timeId <= 0 || ActivityScheduleService.IsOpen(timeId, DateTimeOffset.UtcNow);
        }


        // Consumer-visible mission progress. Core publishes the Theatre6 missions in the login task
        // snapshot and in its own mission pushes; the generic per-request sync does not carry them.
        public JObject PublicTaskRecord(int taskId)
        {
            JObject? record = LoginTaskRecords()
                .Concat(Pushes.Where(push => push.Name == "NotifyTask")
                    .SelectMany(push => (push.Body["Tasks"]?["Tasks"] as JArray ?? []).Children<JObject>()))
                .LastOrDefault(task => task.Value<int>("Id") == taskId);
            if (record is null)
                throw new InvalidDataException($"{Name}: the login task snapshot never published mission {taskId}.");
            return record;
        }

        // The login task snapshot Main's TaskModule builds, which appends Core's Theatre6 missions.
        // The builder publishes a LIST of login tasks, so the payload is parsed as an array.
        public IEnumerable<JObject> LoginTaskRecords()
        {
            object? payload = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
                "BuildTaskData", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Session)])
                .Invoke(null, [Session]);
            if (payload is null) return [];
            JToken serialized = JToken.Parse(MessagePackSerializer.ConvertToJson(
                MessagePackSerialize(payload.GetType(), payload)));
            return serialized is JArray list ? list.Children<JObject>() : [];
        }

        // The authored choice-count mission of the permanent tab and its target cap.
        public (int TaskId, int Target) ChoiceMission()
        {
            foreach (int taskId in PermanentMissions(707))
            {
                TaskTable? task = TableReaderV2.Parse<TaskTable>().FirstOrDefault(row => Convert.ToInt32(row.Id) == taskId);
                if (task is null) continue;
                int conditionId = Convert.ToInt32(task.Condition);
                if (conditionId <= 0) continue;
                AscNet.Table.V2.share.task.ConditionTable? condition = TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>()
                    .FirstOrDefault(row => Convert.ToInt32(row.Id) == conditionId);
                if (condition is null || Convert.ToInt32(condition.Type) != 136004 || condition.Params.Count == 0) continue;
                return (taskId, Convert.ToInt32(condition.Params[0]));
            }

            throw new InvalidDataException($"{Name}: the permanent 707 tab authors no choice-count mission.");
        }

        public int TaskScheduleValue(int taskId) => (PublicTaskRecord(taskId)["Schedule"] as JArray ?? [])
            .Children<JObject>().Select(entry => entry.Value<int>("Value")).DefaultIfEmpty(-1).Max();

        public int MissionProgress(int difficultyId) => State.PassDiffRecords.GetValueOrDefault(difficultyId);

        public int? MissionDifficulty(int difficultyId) => TableReaderV2.Parse<Theatre6RewardTable>()
            .Where(row => Convert.ToInt32(row.TaskTimeLimitId ?? 0) > 0)
            .SelectMany(row => PermanentMissions(Convert.ToInt32(row.TaskTimeLimitId)))
            .Select(taskId => TableReaderV2.Parse<TaskTable>().FirstOrDefault(task => Convert.ToInt32(task.Id) == taskId))
            .Where(task => task is not null && Convert.ToInt32(task.Condition) > 0)
            .Select(task => Convert.ToInt32(task!.Condition))
            .SelectMany(conditionId => TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>()
                .Where(condition => Convert.ToInt32(condition.Id) == conditionId && Convert.ToInt32(condition.Type) == 136009
                    && condition.Params.Count > 0 && Convert.ToInt32(condition.Params[0]) == difficultyId))
            .Select(condition => (int?)difficultyId)
            .FirstOrDefault();

        public long TotalInventoryCount() => Session.inventory.Items.Sum(item => item.Count);

        // An occupied equip slot that satisfies the runtime swap predicate for a Bag-held skill.
        private (int Slot, int Position) AuthoredSwapDestination(string modeKey, int skillId)
        {
            HashSet<int> authored = AuthoredSlotTypes(modeKey, skillId);
            int sourceFamily = SkillFamily(skillId);
            foreach (JObject skill in (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>())
            {
                int slot = skill.Value<int>("SlotType");
                if (slot == 4 || !authored.Contains(slot)) continue;
                if (SkillFamily(skill.Value<int>("SkillId")) == sourceFamily) continue;
                return (slot, skill.Value<int>("Position"));
            }

            return (0, 0);
        }

        public int SkillAt(string modeKey, int slotType, int position) => (Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>().Where(skill => skill.Value<int>("SlotType") == slotType && skill.Value<int>("Position") == position)
            .Select(skill => skill.Value<int>("SkillId")).DefaultIfEmpty(0).First();

        // Equipped skills as id@slot:position, used as diagnostic context for swap failures.
        public string EquippedSkillTrace(string modeKey) => string.Join("|",
            (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                .Where(skill => skill.Value<int>("SlotType") != 4)
                .Select(skill => $"{skill.Value<int>("SkillId")}@{skill.Value<int>("SlotType")}:{skill.Value<int>("Position")}"));

        // True when another equipped (non-bag) skill shares the family of the given skill.
        private bool HasOtherEquippedFamily(string modeKey, int skillId)
        {
            int family = SkillFamily(skillId);
            return (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                .Any(skill => skill.Value<int>("SkillId") != skillId && skill.Value<int>("SlotType") != 4
                    && SkillFamily(skill.Value<int>("SkillId")) == family);
        }

        private static int SkillFamily(int skillId) => Convert.ToInt32(TableReaderV2.Parse<Theatre6SkillTable>()
            .Single(row => Convert.ToInt32(row.Id) == skillId).SkillKey);

        // Live buff records of the run snapshot. Core serialises the live map as a uid-keyed object,
        // but an empty or list-shaped payload must not break the traversal.
        private IEnumerable<JObject> LiveBuffs(string modeKey) => Mode(modeKey)["Buffs"] switch
        {
            JObject map => map.Properties().Select(property => property.Value).OfType<JObject>(),
            JArray array => array.Children<JObject>(),
            _ => []
        };

        private static Theatre6StageBuffTable BuffRow(int buffId) => TableReaderV2.Parse<Theatre6StageBuffTable>()
            .Single(row => Convert.ToInt32(row.Id) == buffId);

        private static Theatre6SkillTable SkillRow(int skillId) => TableReaderV2.Parse<Theatre6SkillTable>()
            .Single(row => Convert.ToInt32(row.Id) == skillId);

        private static int AuthoredSkillLevel(int skillId) => Convert.ToInt32(SkillRow(skillId).Level);

        // Next authored level of the same family (SkillKey), mirroring the runtime's own lookup.
        private static int NextLevelSkillId(int skillId)
        {
            int key = SkillFamily(skillId);
            int level = AuthoredSkillLevel(skillId);
            return TableReaderV2.Parse<Theatre6SkillTable>()
                .Where(row => Convert.ToInt32(row.SkillKey) == key && Convert.ToInt32(row.Level) == level + 1)
                .Select(row => Convert.ToInt32(row.Id)).DefaultIfEmpty(0).First();
        }

        // Chances authored on a star-up buff (StageBuff effect 12, BuffEffectParams[0]).
        private static int StarUpChances(int buffId) => BuffRow(buffId).BuffEffectParams is { Count: > 0 } parameters
            ? Math.Max(1, Convert.ToInt32(parameters[0]))
            : 1;

        // Owned skill the authored star-up limits accept: a next level exists, the level limit and the
        // quality limit of the buff's parameters are respected.
        private int UpgradableOwnedSkill(string modeKey, int buffId)
        {
            List<int> parameters = BuffRow(buffId).BuffEffectParams ?? [];
            int levelLimit = parameters.Count > 2 ? Convert.ToInt32(parameters[2]) : 0;
            int qualityLimit = parameters.Count > 3 ? Convert.ToInt32(parameters[3]) : 0;
            return (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                .Select(skill => skill.Value<int>("SkillId"))
                .Where(id => id > 0 && NextLevelSkillId(id) > 0
                    && (levelLimit <= 0 || AuthoredSkillLevel(id) < levelLimit)
                    && (qualityLimit <= 0 || Convert.ToInt32(SkillRow(id).Quality) <= qualityLimit))
                .DefaultIfEmpty(0).First();
        }

        // An owned skill that can be sold: a bag entry is preferred so selling never disturbs an
        // equipped slot the later move/swap coverage depends on.
        private int SellableOwnedSkill(string modeKey, int excludeId) => (Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>()
            .Where(skill => skill.Value<int>("SkillId") > 0 && skill.Value<int>("SkillId") != excludeId)
            .OrderBy(skill => skill.Value<int>("SlotType") == 4 ? 0 : 1)
            .Select(skill => skill.Value<int>("SkillId")).DefaultIfEmpty(0).First();

        // Authored destination slots for one skill family, from the client's own config sort lists.
        public HashSet<int> AuthoredSlotTypes(string modeKey, int skillId)
        {
            Theatre6SkillTable skill = TableReaderV2.Parse<Theatre6SkillTable>()
                .Single(row => Convert.ToInt32(row.Id) == skillId);
            string key = Convert.ToInt32(skill.Type) switch
            {
                1 => "ActiveSkillSlotSort",
                2 => "ClashSkillSlotSort",
                3 => "UltraCalcSkillSlotSort",
                4 => "InsertSkillSlotSort",
                _ => "ActiveSkillSlotSort"
            };
            return TableReaderV2.Parse<Theatre6ConfigTable>().Single(row => row.Key == key).Values.ToHashSet();
        }

        // Destination from the runtime's own predicate: the Bag accepts any owned skill, and an
        // equip destination additionally requires the authored install slots for the skill's family
        // and no equipped copy of that family; a free position wins, otherwise swap with an occupant
        // of a different family whose own install slots still accept the source slot.
        private (int Slot, int Position) AuthoredDestination(string modeKey, int skillId, int sourceSlot, int sourcePosition)
        {
            int freeBag = FreeSlotPosition(modeKey, 4);
            if (freeBag > 0) return (4, freeBag);

            HashSet<int> authored = AuthoredSlotTypes(modeKey, skillId);
            int sourceFamily = SkillFamily(skillId);
            // The runtime rejects an equip destination outright when another equipped skill already
            // shares the mover's family (EquippedFamilyConflicts -> 20423072), so that branch is
            // never requested while such a copy exists.
            if (HasOtherEquippedFamily(modeKey, skillId)) return (0, 0);
            foreach (JObject skill in (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>())
            {
                int slot = skill.Value<int>("SlotType");
                if (slot == 4 || !authored.Contains(slot)) continue;
                if (slot == sourceSlot && skill.Value<int>("Position") == sourcePosition) continue;
                if (SkillFamily(skill.Value<int>("SkillId")) == sourceFamily) continue;
                if (sourceSlot != 4 && !AuthoredSlotTypes(modeKey, skill.Value<int>("SkillId")).Contains(sourceSlot)) continue;
                return (slot, skill.Value<int>("Position"));
            }

            return (0, 0);
        }

        public IEnumerable<JObject> DealtGoods(string modeKey) => (Room(modeKey)["ShopGoods"] as JArray ?? []).Children<JObject>();

        // Authored acquisition cap of a relic the run already holds: LimitCount from Theatre6AttrPack
        // against the owned copy count in the run snapshot. Returns (0, 0) when nothing is capped yet.
        private (int PackId, int Cap) CappedRelic(string modeKey) => RequiemAttrPacks(Mode(modeKey))
            .Where(pack => TableReaderV2.Parse<Theatre6AttrPackTable>()
                .Any(row => Convert.ToInt32(row.Id) == pack.PackId && Convert.ToInt32(row.LimitCount ?? 0) > 0
                    && pack.Num >= Convert.ToInt32(row.LimitCount ?? 0)))
            .Select(pack => (pack.PackId, pack.Num)).DefaultIfEmpty((0, 0)).First();

        // Focused regression for the capped-relic offer path (seed70): an offer that names a relic the
        // run already holds at its authored LimitCount can never be acquired, so no observed offer may
        // contain one, an uncapped relic offer must still exist and be claimable through the real shop
        // handler, and every owned pack must stay within its authored cap. No refresh is issued here;
        // the offers inspected are the ones the natural walk already generated.
        public void AssertCappedOffersRespectCaps(string modeKey, string label)
        {
            HashSet<int> capped = RequiemAttrPacks(Mode(modeKey))
                .Where(pack => TableReaderV2.Parse<Theatre6AttrPackTable>()
                    .Any(row => Convert.ToInt32(row.Id) == pack.PackId && Convert.ToInt32(row.LimitCount ?? 0) > 0
                        && pack.Num >= Convert.ToInt32(row.LimitCount ?? 0)))
                .Select(pack => pack.PackId).ToHashSet();
            if (capped.Count == 0)
                return;
            JObject room = Room(modeKey);
            List<int> offered = (room["LeftRewards"] as JArray ?? []).Children<JObject>()
                .Concat((room["RightRewards"] as JArray ?? []).Children<JObject>())
                .Select(reward => reward.Value<int>("AttrPack"))
                .Concat(DealtGoods(modeKey).Where(goods => goods.Value<int>("Type") == 2)
                    .Select(goods => goods.Value<int>("GoodId")))
                .Where(packId => packId > 0).ToList();
            Require(offered.All(packId => !capped.Contains(packId)),
                $"{label}: offers must exclude relics already held at their authored cap [{string.Join(",", capped)}]");
            foreach (int packId in capped)
            {
                int cap = Convert.ToInt32(TableReaderV2.Parse<Theatre6AttrPackTable>()
                    .Single(row => Convert.ToInt32(row.Id) == packId).LimitCount ?? 0);
                Require(AttrPackCount(modeKey, packId) <= cap,
                    $"{label}: owned relic {packId} must stay within its authored cap {cap}");
            }

            // An uncapped dealt relic must still be claimable through the ordinary purchase path.
            JObject? claimable = DealtGoods(modeKey).FirstOrDefault(goods => goods.Value<int>("Type") == 2
                && !goods.Value<bool>("IsSell") && !goods.Value<bool>("IsLock") && !capped.Contains(goods.Value<int>("GoodId"))
                && RequiemGoodPrice(goods) >= 0 && Gold(modeKey) >= RequiemGoodPrice(goods));
            if (claimable is null)
                return;
            int claimId = claimable.Value<int>("GoodId");
            int before = AttrPackCount(modeKey, claimId);
            Call("Theatre6ShopGoodBuyRequest", Req(("Pos", claimable.Value<int>("Position"))));
            AssertEqual(true, AttrPackCount(modeKey, claimId) > before,
                $"{label}: an uncapped relic offer stays claimable through the shop handler");
        }

        public int LockablePosition(string modeKey)
        {
            HashSet<int> dealt = DealtGoods(modeKey).Where(goods => !goods.Value<bool>("IsSell"))
                .Select(goods => goods.Value<int>("Position")).ToHashSet();
            return dealt.Count > 0 ? dealt.Min() : throw new InvalidDataException($"{Name}: the authored shop dealt no position.");
        }

        public int ForeignShopPosition(string modeKey)
        {
            HashSet<int> dealt = DealtGoods(modeKey).Select(goods => goods.Value<int>("Position")).ToHashSet();
            for (int position = 0; position < dealt.Count + 2; position++)
                if (!dealt.Contains(position)) return position;
            return dealt.Count + 2;
        }

        private static int RequiemGoodPrice(JObject good) => good.Value<int>("Type") switch
        {
            1 => TableReaderV2.Parse<Theatre6SkillTable>()
                .SingleOrDefault(row => Convert.ToInt32(row.Id) == good.Value<int>("GoodId")) is { } skill ? Convert.ToInt32(skill.BuyPrice) : -1,
            2 => TableReaderV2.Parse<Theatre6AttrPackTable>()
                .SingleOrDefault(row => Convert.ToInt32(row.Id) == good.Value<int>("GoodId")) is { } pack ? Convert.ToInt32(pack.BuyPrice) : -1,
            _ => -1
        };

        private static Theatre6StageShopTable ShopRow(int shopId) => TableReaderV2.Parse<Theatre6StageShopTable>()
            .Single(row => Convert.ToInt32(row.Id) == shopId);

        public int Gold(string modeKey) => Mode(modeKey).Value<int>("GoldAmount");

        public long InventoryFingerprint() => Session.inventory.Items
            .OrderBy(item => item.Id)
            .Aggregate(17L, (hash, item) => unchecked((hash * 31) + item.Id + item.Count));

        private static int FirstAddedSkill(IEnumerable<JObject> updates) => updates
            .SelectMany(update => update["AddSkill"] is JObject add ? new[] { add } : Array.Empty<JObject>())
            .Select(add => add.Value<int>("SkillId")).FirstOrDefault();

        public bool SkillOwned(string modeKey, int skillId) => (Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>().Any(skill => skill.Value<int>("SkillId") == skillId);

        // Owned copies of one skill id; a queued or stored duplicate legitimately keeps the id owned.
        private int OwnedCopies(string modeKey, int skillId) => (Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>().Count(skill => skill.Value<int>("SkillId") == skillId);

        // Owned copies of one authored skill family (SkillKey), across every slot.
        public int OwnedFamilyCopies(string modeKey, int skillId) => (Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>().Count(skill => skill.Value<int>("SkillId") > 0
                && SkillFamily(skill.Value<int>("SkillId")) == SkillFamily(skillId));

        private int OwnedPosition(string modeKey, int skillId) => (Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>().Where(skill => skill.Value<int>("SkillId") == skillId)
            .Select(skill => skill.Value<int>("Position")).DefaultIfEmpty(0).First();

        public int AttrPackCount(string modeKey, int packId) => RequiemAttrPacks(Mode(modeKey))
            .Where(pack => pack.PackId == packId).Select(pack => pack.Num).DefaultIfEmpty(0).Sum();

        public JObject? OwnedSkillForMove(string modeKey) => (Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>().FirstOrDefault(skill => skill.Value<int>("SkillId") > 0);

        public int FreeSlotPosition(string modeKey, int slotType)
        {
            int limit = slotType switch
            {
                1 => 1,
                2 => RequiemConfigValue("ActiveSkillSlotLimit"),
                3 => RequiemConfigValue("InsertSkillSlotLimit"),
                _ => RequiemConfigValue("SkillBagSlotLimit")
            };
            for (int position = 1; position <= limit; position++)
                if (SkillPositionFree(modeKey, slotType, position)) return position;
            return 0;
        }

        public bool SkillPositionFree(string modeKey, int slotType, int position) => !(Mode(modeKey)["Skills"] as JArray ?? [])
            .Children<JObject>().Any(skill => skill.Value<int>("SlotType") == slotType && skill.Value<int>("Position") == position);

        public bool SkillInstalled(string modeKey, int skillId, int slotType, int position) =>
            (Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                .Any(skill => skill.Value<int>("SkillId") == skillId
                    && skill.Value<int>("SlotType") == slotType && skill.Value<int>("Position") == position);


        // Authored theatre6 preview bundle: Activity.ShowItems/ShowNum is the only catalog source.
        private static List<(int ItemId, int Amount)> RequiemPreviewBundles()
        {
            Theatre6ActivityTable activity = TableReaderV2.Parse<Theatre6ActivityTable>().First();
            List<(int, int)> bundles = [];
            for (int index = 0; index < activity.ShowItems.Count; index++)
            {
                int itemId = activity.ShowItems[index];
                if (itemId <= 0) continue;
                bundles.Add((itemId, index < activity.ShowNum.Count ? activity.ShowNum[index] : 1));
            }

            return bundles;
        }

        public int ShowItemCount() => RequiemPreviewBundles().Count;
        public int ShowItemId(int index) => RequiemPreviewBundles()[index].ItemId;
        public int ShowItemAmount(int index) => RequiemPreviewBundles()[index].Amount;

        public int ShowItemIndex(int itemId)
        {
            int index = RequiemPreviewBundles().FindIndex(bundle => bundle.ItemId == itemId);
            if (index < 0)
                throw new InvalidDataException($"{Name}: reward item {itemId} is not an authored preview bundle.");
            return index;
        }

        // Policy currency: the item every task of the paired mission group rewards. The harness
        // grants it so the purchase is funded from the authored mission economy, never fabricated.
        public int FundRewardShop(uint shopId)
        {
            (int currency, long total, _) = RequiemRewardShopEconomy(shopId);
            Item wallet = Session.inventory.Items.FirstOrDefault(item => item.Id == currency)
                ?? new Item { Id = currency, Count = 0 };
            if (!Session.inventory.Items.Contains(wallet)) Session.inventory.Items.Add(wallet);
            wallet.Count = Math.Max(wallet.Count, total);
            SaveFixture();
            return currency;
        }

        public long RewardShopUnitPrice(uint shopId) => RequiemRewardShopEconomy(shopId).UnitPrice;

        private (int Currency, long Total, long UnitPrice) RequiemRewardShopEconomy(uint shopId)
        {
            List<Theatre6RewardTable> tabs = TableReaderV2.Parse<Theatre6RewardTable>();
            int shopIndex = tabs.Where(row => row.ShopId is > 0).OrderBy(row => row.Priority).ToList()
                .FindIndex(row => row.ShopId == shopId);
            Require(shopIndex >= 0, $"No authored Theatre6 reward shop {shopId}.");
            Theatre6RewardTable missionTab = tabs.Where(row => row.TaskTimeLimitId is > 0)
                .OrderBy(row => row.Priority).ElementAt(shopIndex);
            MethodInfo getRewardGoods = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler")
                .GetMethod("GetRewardGoods", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(int)], null)
                ?? throw new MissingMethodException("AscNet.GameServer.Handlers.RewardHandler", "GetRewardGoods");
            Dictionary<int, long> totals = [];
            int taskCount = 0;
            TaskTimeLimitTable timeLimit = TableReaderV2.Parse<TaskTimeLimitTable>()
                .Single(row => Convert.ToInt32(row.Id) == Convert.ToInt32(missionTab.TaskTimeLimitId));
            foreach (int taskId in timeLimit.TaskId.Where(id => id > 0))
            {
                TaskTable? task = TableReaderV2.Parse<TaskTable>().FirstOrDefault(row => Convert.ToInt32(row.Id) == taskId);
                if (task is null || Convert.ToInt32(task.RewardId ?? 0) <= 0) continue;
                taskCount++;
                foreach (RewardGoodsTable goods in (List<RewardGoodsTable>)getRewardGoods.Invoke(null, [Convert.ToInt32(task.RewardId)])!)
                {
                    int itemId = goods.TemplateId > 0 ? goods.TemplateId : goods.Id;
                    if (itemId <= 0) continue;
                    totals[itemId] = totals.GetValueOrDefault(itemId) + goods.Count;
                }
            }

            if (taskCount == 0 || totals.Count == 0)
                throw new InvalidDataException($"{Name}: paired mission group for reward shop {shopId} rewards nothing.");
            KeyValuePair<int, long> currency = totals.Single();
            long unitPrice = Math.Max(1, currency.Value / RequiemPreviewBundles().Count);
            return (currency.Key, currency.Value, unitPrice);
        }


        // Journal fault fixtures need one shop request this authored room must accept.
        public (string Request, Dictionary<string, object?> Payload) AffordableShopRequest(string modeKey)
        {
            JObject room = Room(modeKey);
            Theatre6StageShopTable shop = ShopRow(room.Value<int>("ShopId"));
            int buySanTimes = room.Value<int>("BuySanTimes");
            int sanPrice = Convert.ToInt32(shop.BuySanBasePrice) + (buySanTimes * Convert.ToInt32(shop.BuySanAddPrice));
            if (Gold(modeKey) >= sanPrice && Mode(modeKey).Value<int>("San") < Mode(modeKey).Value<int>("MaxSan"))
                return ("Theatre6ShopBuySanRequest", Req(("BuySanTimes", buySanTimes)));
            int expectedFresh = room.Value<int>("ShopFreshCount");
            int refreshPrice = Convert.ToInt32(shop.BaseRereshPrice) + (expectedFresh * Convert.ToInt32(shop.RefreshAddPrice));
            if (Gold(modeKey) >= refreshPrice)
                return ("Theatre6ShopFreshRequest", Req(("ShopFreshCount", expectedFresh)));
            JObject? affordable = DealtGoods(modeKey)
                .Where(goods => !goods.Value<bool>("IsSell") && !goods.Value<bool>("IsLock"))
                .FirstOrDefault(goods => RequiemGoodPrice(goods) >= 0 && Gold(modeKey) >= RequiemGoodPrice(goods));
            if (affordable is not null)
                return ("Theatre6ShopGoodBuyRequest", Req(("Pos", affordable.Value<int>("Position"))));
            int fallbackPosition = LockablePosition(modeKey);
            return ("Theatre6ShopGoodLockRequest",
                Req(("Pos", fallbackPosition), ("IsLock", ShopGood(modeKey, fallbackPosition).Value<bool>("IsLock"))));
        }
    }
}
