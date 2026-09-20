using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.lotto;
using AscNet.Table.V2.share.wheelchairmanual;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateWheelchairManualLottoCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        WheelchairManualActivityTable activity = TableReaderV2.Parse<WheelchairManualActivityTable>()
            .Single(row => row.LottoId > 0 && row.TimeId > 0);
        LottoTable lotto = TableReaderV2.Parse<LottoTable>().Single(row => row.Id == activity.LottoId);
        LottoPrimaryTable primary = TableReaderV2.Parse<LottoPrimaryTable>()
            .Single(row => row.TimeId == activity.TimeId && row.LottoIdList.Contains(lotto.Id));
        Dictionary<int, LottoRewardTable> rewards = TableReaderV2.Parse<LottoRewardTable>()
            .Where(row => row.LottoId == lotto.Id).ToDictionary(row => row.Id);
        Dictionary<int, LottoBuyTicketRuleTable> rules = TableReaderV2.Parse<LottoBuyTicketRuleTable>()
            .ToDictionary(row => row.Id);
        LottoBuyTicketRuleTable firstRule = rules[lotto.BuyTicketRuleIdList[0]];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
        IMongoCollection<Player> collection = DispatchProxy.Create<IMongoCollection<Player>, LottoPlayerSaveProxy>();
        LottoPlayerSaveProxy saves = (LottoPlayerSaveProxy)(object)collection;
        typeof(MongoCollectionOverride).GetMethod("SetStaticField", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [typeof(Player).GetField("collection", BindingFlags.Static | BindingFlags.Public)!, collection]);
        int packetId = 47_815_000;

        T Dispatch<T>(LoopbackSessionHarness harness, object request, string name, bool expectItems = false, bool allowPush = true)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(request.GetType().Name, harness.Session, id, request);
            bool items = false;
            for (int index = 0; index < 64; index++)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push)
                {
                    AssertEqual(true, allowPush, name + " rejected request emits no push");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (push.Name == nameof(NotifyItemDataList))
                    {
                        items = true;
                        NotifyItemDataList update = MessagePackSerializer.Deserialize<NotifyItemDataList>(push.Content);
                        foreach (var item in update.ItemDataList)
                            AssertEqual(Balance(harness, item.Id), item.Count, name + " pushed committed balance");
                    }
                    continue;
                }
                AssertEqual(Packet.ContentType.Response, packet.Type, name + " response packet");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, name + " correlation");
                AssertEqual(typeof(T).Name, response.Name, name + " response name");
                JObject payload = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                AssertEqual(JTokenType.Integer, payload["Code"]?.Type, name + " Lua success comparison requires numeric Code");
                if (typeof(T) == typeof(LottoResponse) && payload.Value<int>("Code") == 0)
                {
                    AssertEqual(JTokenType.Integer, payload["LottoRewardId"]?.Type, name + " Lua pool update requires selected reward ID");
                    AssertEqual(JTokenType.Integer, payload["ExtraRewardState"]?.Type, name + " Lua pool update requires extra state");
                    foreach (string field in new[] { "RewardList", "ExtraRewardList", "LottoRecords" })
                        AssertEqual(JTokenType.Array, payload[field]?.Type, name + " Lua consumer requires " + field + " array");
                    JObject reward = (JObject)((JArray)payload["RewardList"]!).Single();
                    foreach (string field in new[] { "TemplateId", "Count", "RewardType", "Id", "ConvertFrom", "ShowQuality" })
                        AssertEqual(JTokenType.Integer, reward[field]?.Type, name + " Lua reward rendering requires numeric " + field);
                }
                if (expectItems)
                    AssertEqual(true, items, name + " inventory notification precedes response");
                AssertNoAvailablePacket(harness, name + " no notification after callback");
                return MessagePackSerializer.Deserialize<T>(response.Content);
            }
            throw new InvalidDataException(name + " missing response");
        }
        long Balance(LoopbackSessionHarness harness, int itemId) =>
            harness.Session.inventory.Items.SingleOrDefault(item => item.Id == itemId)?.Count ?? 0;
        LottoInfoResponse.LottoInfo Info(LoopbackSessionHarness harness)
        {
            LottoInfoResponse response = Dispatch<LottoInfoResponse>(harness, new LottoInfoRequest(), "lotto relog info");
            AssertEqual(0, response.Code, "lotto info succeeds");
            return response.LottoInfos.Single(row => row.Id == lotto.Id);
        }
        LottoBuyTicketRequest Ticket(int draw, int key) => new()
        {
            LottoPrimaryId = primary.Id, TicketId = lotto.BuyTicketRuleIdList[draw], TicketKey = key
        };
        void Reject(LoopbackSessionHarness harness, object request, string name)
        {
            string inventory = harness.Session.inventory.ToJson();
            string character = harness.Session.character.ToJson();
            string progress = harness.Session.player.Lotto.ToJson();
            int code = request is LottoRequest
                ? Dispatch<LottoResponse>(harness, request, name, allowPush: false).Code
                : Dispatch<LottoBuyTicketResponse>(harness, request, name, allowPush: false).Code;
            AssertEqual(true, code != 0, name + " rejected");
            AssertEqual(inventory, harness.Session.inventory.ToJson(), name + " preserves inventory and receipts");
            AssertEqual(character, harness.Session.character.ToJson(), name + " preserves character");
            AssertEqual(progress, harness.Session.player.Lotto.ToJson(), name + " preserves pool");
        }

        // Each payment option is exercised by a different player, from empty pool through exhaustion.
        for (int key = 1; key <= firstRule.UseItemId.Count; key++)
        {
            int uid = 47_815 + key;
            Character character = new() { Uid = uid, Characters = [], Equips = [], Fashions = [], Partners = [] };
            Dictionary<int, long> expected = [];
            foreach (int ruleId in lotto.BuyTicketRuleIdList.Take(rewards.Count).Prepend(firstRule.Id))
            {
                LottoBuyTicketRuleTable rule = rules[ruleId];
                int currency = rule.UseItemId[key - 1];
                expected[currency] = expected.GetValueOrDefault(currency) + rule.UseItemCount[key - 1];
            }
            Inventory inventory = new()
            {
                Uid = uid, Items = expected.Select(pair => new Item { Id = pair.Key, Count = pair.Value }).ToList()
            };
            using LoopbackSessionHarness harness = new(character, inventory: inventory, sessionId: $"manual-lotto-{key}");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            AssertEqual(0, Info(harness).LottoRewards.Count, "distinct player starts with untouched pool");
            Reject(harness, new LottoRequest { Id = int.MaxValue }, "unknown draw primary");
            Reject(harness, new LottoBuyTicketRequest { LottoPrimaryId = int.MaxValue, TicketId = firstRule.Id, TicketKey = key }, "unknown ticket primary");
            Reject(harness, new LottoBuyTicketRequest { LottoPrimaryId = primary.Id, TicketId = int.MaxValue, TicketKey = key }, "unknown ticket rule");
            Reject(harness, new LottoBuyTicketRequest { LottoPrimaryId = primary.Id, TicketId = lotto.BuyTicketRuleIdList[1], TicketKey = key }, "future ticket rule");
            foreach (int invalidKey in new[] { 0, -1, firstRule.UseItemId.Count + 1 })
                Reject(harness, Ticket(0, invalidKey), "invalid ticket payment key");
            Reject(harness, new LottoRequest { Id = primary.Id }, "draw without tickets");
            int paymentId = firstRule.UseItemId[key - 1];
            Item payment = inventory.Items.Single(item => item.Id == paymentId);
            long funded = payment.Count;
            payment.Count = firstRule.UseItemCount[key - 1] - 1;
            Reject(harness, Ticket(0, key), "insufficient ticket payment");
            payment.Count = funded;

            void Buy(int slot)
            {
                LottoBuyTicketRuleTable rule = rules[lotto.BuyTicketRuleIdList[slot]];
                LottoBuyTicketResponse response = Dispatch<LottoBuyTicketResponse>(harness, Ticket(slot, key), "buy configured tickets", true);
                AssertEqual(0, response.Code, "ticket purchase succeeds");
                AssertEqual(lotto.ConsumeId, response.ItemId, "ticket response currency");
                AssertEqual(rule.TargetItemCount, response.ItemCount, "ticket response table amount");
                int currency = rule.UseItemId[key - 1];
                expected[currency] -= rule.UseItemCount[key - 1];
                expected[lotto.ConsumeId] = expected.GetValueOrDefault(lotto.ConsumeId) + rule.TargetItemCount;
                AssertBalances();
            }
            void AssertBalances()
            {
                foreach (int itemId in expected.Keys.Concat(inventory.Items.Select(item => item.Id)).Distinct())
                    AssertEqual(expected.GetValueOrDefault(itemId), Balance(harness, itemId), $"player {key} cumulative item {itemId}");
            }
            // Buying again before drawing is legal: purchase count is not a guessed per-slot cap.
            Buy(0);
            HashSet<int> drawn = [];
            bool openedAutoBox = false;
            for (int slot = 0; slot < rewards.Count; slot++)
            {
                Buy(slot);
                LottoResponse response = Dispatch<LottoResponse>(harness, new LottoRequest { Id = primary.Id }, "draw configured pool", true);
                AssertEqual(0, response.Code, "draw succeeds");
                AssertEqual(true, rewards.TryGetValue(response.LottoRewardId, out LottoRewardTable? reward), "draw selects configured reward");
                AssertEqual(true, drawn.Add(response.LottoRewardId), "pool reward cannot repeat");
                AssertEqual(true, reward!.Weights[slot] > 0, "zero-weight rewards cannot appear before eligible slot");
                RewardGoods goods = response.RewardList.Single();
                AssertEqual(reward.TemplateId, goods.TemplateId, "draw response table template");
                AssertEqual(reward.Count, goods.Count, "draw response table quantity");
                expected[lotto.ConsumeId] -= lotto.ConsumeCountList[slot];
                expected[reward.TemplateId] = expected.GetValueOrDefault(reward.TemplateId) + reward.Count;
                AssertBalances();
                var autoGoods = AssertParentAwardAutoUse(harness, response.RewardList, ref packetId);
                if (autoGoods.Count > 0)
                {
                    openedAutoBox = true;
                    expected[reward.TemplateId] -= reward.Count;
                    foreach (var material in autoGoods)
                        expected[material.TemplateId] = expected.GetValueOrDefault(material.TemplateId) + material.Count;
                    AssertBalances();
                }
                AssertEqual(0, response.ExtraRewardState, "unconfigured extra reward remains unavailable");
                AssertEqual(0, response.ExtraRewardList.Count, "no invented extra reward");
                AssertEqual(slot + 1, response.LottoRecords.Count, "draw response contains cumulative history");
                foreach (LottoRewardTable selected in drawn.Select(id => rewards[id]))
                    AssertEqual(1, response.LottoRecords.Count(record => record.RewardGoods.TemplateId == selected.TemplateId
                        && record.RewardGoods.Count == selected.Count), "history contains each selected reward exactly once");
                Player persisted = BsonSerializer.Deserialize<Player>(saves.Persisted!);
                AssertEqual(true, drawn.SetEquals(persisted.Lotto.Infos.Single().LottoRewards), "successful draw persists pool membership");
                using LoopbackSessionHarness relog = new(BsonSerializer.Deserialize<Character>(character.ToBson()), persisted,
                    BsonSerializer.Deserialize<Inventory>(inventory.ToBson()), $"manual-lotto-relog-{key}-{slot}");
                relog.Session.stage = CreateLoginAccountCompatibilityStage(uid);
                LottoInfoResponse.LottoInfo info = Info(relog);
                AssertEqual(true, drawn.SetEquals(info.LottoRewards), "relog advertises drawn pool");
                AssertEqual(MessagePackSerializer.SerializeToJson(response.LottoRecords),
                    MessagePackSerializer.SerializeToJson(info.LottoRecords), "relog preserves history and draw times");
            }
            AssertEqual(true, drawn.SetEquals(rewards.Keys), "entire configured pool exhausted uniquely");
            AssertEqual(true, openedAutoBox, "Exhausted lotto exercises parent-awarded auto boxes");
            Reject(harness, new LottoRequest { Id = primary.Id }, "exhausted draw");
            Reject(harness, Ticket(rewards.Count - 1, key), "exhausted ticket purchase");
        }

        // Freeze a real operation before the receipt, then interrupt the final player write.
        foreach (bool buying in new[] { true, false })
        {
            int uid = buying ? 47_830 : 47_831;
            Character character = new() { Uid = uid, Characters = [], Equips = [], Fashions = [], Partners = [] };
            Inventory inventory = new()
            {
                Uid = uid,
                Items = [new Item { Id = firstRule.UseItemId[0], Count = firstRule.UseItemCount[0] },
                    new Item { Id = lotto.ConsumeId, Count = buying ? 0 : lotto.ConsumeCountList[0] }]
            };
            using LoopbackSessionHarness harness = new(character, inventory: inventory, sessionId: $"manual-lotto-failure-{buying}");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            _ = Info(harness);
            object request = buying ? Ticket(0, 1) : new LottoRequest { Id = primary.Id };
            string inventoryBefore = inventory.ToJson();
            saves.WritesUntilFailure = 1;
            try
            {
                InvokeRegisteredRequestHandler(request.GetType().Name, harness.Session, ++packetId, request);
                throw new InvalidDataException("Lotto injected final player save unexpectedly succeeded.");
            }
            catch (InvalidDataException exception) when (exception.InnerException is MongoException)
            {
            }
            finally
            {
                saves.WritesUntilFailure = -1;
            }
            AssertNoAvailablePacket(harness, "failed final save emits no success or premature reward push");
            AssertEqual(false, inventoryBefore == inventory.ToJson(), "receipt and debit survive failed player completion");
            Player pending = BsonSerializer.Deserialize<Player>(saves.Persisted!);
            AssertEqual(true, pending.Lotto.Infos.Single().Pending is not null, "durable pending operation survives relog");
            int frozenRewardId = pending.Lotto.Infos.Single().Pending!.RewardId;
            string inventoryAfterReceipt = inventory.ToJson();
            string characterAfterReceipt = character.ToJson();
            using LoopbackSessionHarness retry = new(BsonSerializer.Deserialize<Character>(character.ToBson()), pending,
                BsonSerializer.Deserialize<Inventory>(inventory.ToBson()), $"manual-lotto-retry-{buying}");
            retry.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            if (buying)
            {
                LottoBuyTicketResponse response = Dispatch<LottoBuyTicketResponse>(retry, request, "ticket persisted retry");
                AssertEqual(0, response.Code, "ticket retry completes");
                AssertEqual(firstRule.TargetItemCount, response.ItemCount, "ticket retry reports original grant");
                AssertEqual(lotto.ConsumeId, response.ItemId, "ticket retry reports original currency");
                AssertEqual(1, retry.Session.player.Lotto.Infos.Single().TicketPurchaseCount, "retry commits purchase once");
            }
            else
            {
                LottoResponse response = Dispatch<LottoResponse>(retry, request, "draw persisted retry");
                AssertEqual(0, response.Code, "draw retry completes");
                AssertEqual(frozenRewardId, response.LottoRewardId, "retry cannot reroll the frozen selection");
                LottoRewardTable selected = rewards[response.LottoRewardId];
                AssertEqual((long)selected.Count, Balance(retry, selected.TemplateId), "retry returns the durably granted selection");
                AssertEqual(1, response.LottoRecords.Count, "draw retry records only one draw");
                AssertEqual(0L, Balance(retry, lotto.ConsumeId), "draw retry does not need a second debit");
            }
            AssertEqual(inventoryAfterReceipt, retry.Session.inventory.ToJson(), "pending retry neither debits nor grants again");
            AssertEqual(characterAfterReceipt, retry.Session.character.ToJson(), "pending retry does not duplicate non-item rewards");
            Player completed = BsonSerializer.Deserialize<Player>(saves.Persisted!);
            AssertEqual(true, completed.Lotto.Infos.Single().Pending is null, "retry durably clears pending operation");
        }
    }

    private class LottoPlayerSaveProxy : RecordingMongoCollectionProxy<Player>
    {
        public int WritesUntilFailure { get; set; } = -1;
        public byte[]? Persisted { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IMongoCollection<Player>.ReplaceOne)
                && args?.OfType<Player>().SingleOrDefault() is Player player)
            {
                if (WritesUntilFailure == 0)
                    throw new MongoException("Injected lotto final player-save failure.");
                if (WritesUntilFailure > 0)
                    WritesUntilFailure--;
                object? result = base.Invoke(targetMethod, args);
                Persisted = player.ToBson();
                return result;
            }
            return base.Invoke(targetMethod, args);
        }
    }
}
