using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.biancatheatre;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class BiancaTheatreSelectNodeRequest { public int NodeId { get; set; } public int SlotId { get; set; } }
[MessagePackObject(true)] public sealed class BiancaTheatreSelectNodeResponse : BiancaTheatreResponse { }
[MessagePackObject(true)] public sealed class BiancaTheatreSelectItemRewardRequest { public int InnerItemId { get; set; } }
[MessagePackObject(true)] public sealed class BiancaTheatreSelectItemRewardResponse : BiancaTheatreResponse { }
[MessagePackObject(true)] public sealed class BiancaTheatreRecvFightRewardRequest { public int Uid { get; set; } }
[MessagePackObject(true)] public sealed class BiancaTheatreRecvFightRewardResponse : BiancaTheatreResponse { public List<RewardGoods> RewardGoodsList { get; set; } = []; }
[MessagePackObject(true)] public sealed class BiancaTheatreEndRecvFightRewardResponse : BiancaTheatreResponse { }
[MessagePackObject(true)] public sealed class BiancaTheatreEventNodeNextStepRequest { public int CurEventStepId { get; set; } public int OptionId { get; set; } }
[MessagePackObject(true)] public sealed class BiancaTheatreEventNodeNextStepResponse : BiancaTheatreResponse
{
    public int NextEventStepId { get; set; }
    public int FightTemplateId { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
    public List<int> InnerItemIds { get; set; } = [];
}
[MessagePackObject(true)] public sealed class BiancaTheatreNodeShopBuyItemRequest { public int ShopItemUid { get; set; } }
[MessagePackObject(true)] public sealed class BiancaTheatreNodeShopBuyItemResponse : BiancaTheatreResponse { }
[MessagePackObject(true)] public sealed class BiancaTheatreEndNodeResponse : BiancaTheatreResponse { }
[MessagePackObject(true)] public sealed class NotifyBiancaTheatreAddItem { public List<BiancaTheatreItem> BiancaTheatreItems { get; set; } = []; }
[MessagePackObject(true)] public sealed class NotifyBiancaTheatreNodeNextStep { public int NextStepId { get; set; } public int EventId { get; set; } }
[MessagePackObject(true)] public sealed class NotifyBiancaTheatreVisionChange { public int VisionId { get; set; } }
[MessagePackObject(true)] public sealed class NotifyBiancaTheatreVisionCondition { }
[MessagePackObject(true)] public sealed class NotifyBiancaTheatreFightNodeCountChange { public int FightNodeCount { get; set; } }

internal static partial class BiancaTheatreModule
{
    // XItemConfigs / XBiancaTheatreConfigs currency identities, not captured balances.
    private const int NodeCoin = 96119;
    private const int NodeVision = 96185;
    private static void NodeRequire(bool valid, string message)
    {
        if (!valid) throw new ServerCodeException(message, 1);
    }

    private static BiancaTheatreStep NodeCurrent(Mutation m, params int[] types)
    {
        var step = CurrentStep(m);
        NodeRequire(step != null && types.Contains(step.StepType), "No matching pending theatre step");
        return step!;
    }

    private static BiancaTheatreNodeSlot SelectedNode(Mutation m)
    {
        var step = NodeCurrent(m, 5);
        var slot = step.NodeData?.Slots.SingleOrDefault(x => x.Selected != 0);
        NodeRequire(slot != null, "No selected theatre node");
        return slot!;
    }

    private static List<BiancaTheatreStep> NodeHistory(Mutation m) => m.State.CompletedChapters
        .SelectMany(x => x.Steps).Concat(m.Data.CurChapterDb?.Steps ?? []).ToList();

    private static int NodeArray(IReadOnlyList<int> values, int index) => index < values.Count ? values[index] : 0;

    public static List<int> DrawItemBox(Mutation m, int boxId)
    {
        var box = Rows<BiancaTheatreItemBoxTable>().Single(x => x.Id == boxId);
        var result = new List<int>();
        var items = Rows<BiancaTheatreItemTable>().ToDictionary(x => x.Id);
        var groups = Rows<BiancaTheatreItemGroupTable>();
        foreach (int group in box.GroupId)
        {
            var pool = groups.Where(x => x.GroupId == group && !result.Contains(x.ItemId)
                && ((items[x.ItemId].Repeatable ?? 0) != 0 || !m.Data.Items.Any(i => i.ItemId == x.ItemId))
                && (m.Data.UnlockItemId.Contains(x.ItemId) || Condition(m, items[x.ItemId].UnlockConditionId ?? 0))).ToList();
            if (pool.Count == 0)
                pool = groups.Where(x => x.GroupId == box.SafeGroupId && !result.Contains(x.ItemId)
                    && (m.Data.UnlockItemId.Contains(x.ItemId) || Condition(m, items[x.ItemId].UnlockConditionId ?? 0))).ToList();
            var picked = PickWeighted(pool, x =>
            {
                double weight = x.Weight;
                foreach (var effect in SystemEffects(m).Where(e => e.Type == 8 && (int)e.Params[0] == items[x.ItemId].Quality))
                    weight *= effect.Params[1];
                return checked((int)Math.Round(weight));
            });
            result.Add(picked.ItemId);
        }
        NodeRequire(result.Count > 0, "Empty item box");
        return result;
    }

    public static void AppendItemChoice(Mutation m, IReadOnlyList<int> itemIds, int rootUid = 0, bool extra = false)
    {
        NodeRequire(itemIds.Count > 0 && itemIds.All(id => Rows<BiancaTheatreItemTable>().Any(x => x.Id == id)), "Invalid item choices");
        var step = new BiancaTheatreStep { StepType = extra ? 1 : 2, RootUid = rootUid, IsExtraReward = extra ? 1 : 0, ItemIds = itemIds.ToList() };
        QueueNodeChoice(m, step);
    }

    public static void AppendItemBox(Mutation m, int boxId, int rootUid = 0, bool extra = false) =>
        AppendItemChoice(m, DrawItemBox(m, boxId), rootUid, extra);

    public static void GrantInnerItem(Mutation m, int itemId)
    {
        var row = Rows<BiancaTheatreItemTable>().Single(x => x.Id == itemId);
        var item = new BiancaTheatreItem { Uid = Uid(m), ItemId = itemId };
        m.Data.Items.Add(item);
        m.State.RunItemCount++;
        m.Data.HistoryTotalItemCount++;
        m.Data.HistoryItemObtainRecords[row.Quality] = m.Data.HistoryItemObtainRecords.GetValueOrDefault(row.Quality) + 1;
        m.Push(new NotifyBiancaTheatreAddItem { BiancaTheatreItems = [item] });
        if (row.VisionId is > 0) GrantNodeVision(m, row.VisionId.Value, 1);
        if (row.EffectGroupId is not > 0) return;
        var group = Rows<BiancaTheatreEffectGroupTable>().Single(x => x.Id == row.EffectGroupId);
        foreach (var id in group.SystemEvents)
        {
            var effect = Rows<BiancaTheatreSystemEffectTable>().Single(x => x.Id == id);
            switch (effect.Type)
            {
                case 1: NodeGoods(m, checked((int)effect.Params[0]), checked((int)effect.Params[1])); break;
                case 14:
                    foreach (int drawn in DrawItemBox(m, checked((int)effect.Params[0]))) GrantInnerItem(m, drawn);
                    break;
                case 22:
                    int currency = checked((int)effect.Params[0]);
                    int target = checked((int)effect.Params[1]);
                    long balance = m.Balance(currency);
                    if (balance > target) m.AddCost(currency, checked((int)(balance - target)));
                    else if (balance < target) m.AddGoods(currency, checked((int)(target - balance)));
                    break;
            }
        }
    }

    private static void NodeGoods(Mutation m, int itemId, int count)
    {
        NodeRequire(count >= 0, "Negative theatre reward");
        if ((itemId == NodeCoin && SystemEffects(m).Any(x => x.Type == 23))
            || (itemId == 96120 && SystemEffects(m).Any(x => x.Type == 24))) return;
        if (count != 0) m.AddGoods(itemId, count);
    }

    [RequestPacketHandler("BiancaTheatreSelectItemRewardRequest")]
    public static void BiancaTheatreSelectItemRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreSelectItemRewardRequest, BiancaTheatreSelectItemRewardResponse>(session, packet, (m, req, res) =>
        {
            var step = NodeCurrent(m, 1, 2);
            NodeRequire(step.SelectedItemId == 0 && step.ItemIds.Contains(req.InnerItemId), "Item is not an offered choice");
            step.SelectedItemId = req.InnerItemId;
            GrantInnerItem(m, req.InnerItemId);
            step.Overdue = 1;
            ContinueRun(m);
        });

    [RequestPacketHandler("BiancaTheatreEventNodeNextStepRequest")]
    public static void BiancaTheatreEventNodeNextStepRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreEventNodeNextStepRequest, BiancaTheatreEventNodeNextStepResponse>(session, packet, (m, req, res) =>
        {
            var parent = NodeCurrent(m, 5);
            var slot = SelectedNode(m);
            NodeRequire(slot.SlotType == 2 && slot.CurStepId == req.CurEventStepId && m.State.Fight == null, "Event step is not current");
            var row = Rows<BiancaTheatreEventTable>().Single(x => x.EventId == slot.EventId && x.StepId == slot.CurStepId);
            NodeRequire(row.Type != 5 && row.Type != 7, "Event battle must complete first");
            int next = row.NextStepId ?? 0;
            if (row.Type == 2)
            {
                int option = req.OptionId - 1;
                NodeRequire(option >= 0 && option < row.OptionType.Count, "Invalid event option");
                NodeRequire(Condition(m, NodeArray(row.OptionCondition, option)), "Event option condition is unmet");
                int type = row.OptionType[option];
                int itemType = NodeArray(row.OptionItemType, option);
                int id = NodeArray(row.OptionItemId, option);
                int count = NodeArray(row.OptionItemCount, option);
                NodeRequire(type is 1 or 2 or 3, "Unknown event option type");
                if (type is 1 or 2) EventCost(m, itemType, id, count, type == 1);
                int group = NodeArray(row.OptionNextStepGroupId, option);
                next = group == 0 ? 0 : PickWeighted(Rows<BiancaTheatreEventStepGroupTable>().Where(x => x.GroupId == group), x => x.Weight).StepId;
            }
            else NodeRequire(req.OptionId == 0, "This event has no choices");
            int goods = m.Goods.Count;
            EventRewards(m, row, parent.Uid, res.InnerItemIds);
            slot.PassedStepId.Add(row.StepId);
            RecordEventChoice(m, row.EventId, row.StepId, req.OptionId);
            SetEventStep(m, parent, slot, next);
            res.NextEventStepId = next;
            res.FightTemplateId = slot.FightTemplateId;
            res.RewardGoodsList = NodeGoodsResponse(m, goods);
            if (CurrentStep(m)?.Uid != parent.Uid) return;
            if (next == 0) CompleteNode(m);
        });

    private static void EventCost(Mutation m, int type, int id, int count, bool consume)
    {
        NodeRequire(count >= 0, "Invalid event cost");
        switch (type)
        {
            case 1:
                NodeRequire(m.Balance(id) >= count, "Insufficient event currency");
                if (consume && count > 0)
                {
                    m.AddCost(id, count);
                    if (id == NodeCoin) m.State.TotalCookieSpent = checked(m.State.TotalCookieSpent + count);
                }
                break;
            case 2:
                var items = m.Data.Items.Where(x => x.ItemId == id).Take(count).ToList();
                NodeRequire(items.Count == count, "Missing event item");
                if (consume) foreach (var item in items) m.Data.Items.Remove(item);
                break;
            default: throw new ServerCodeException($"Unsupported event cost type {type}", 1);
        }
    }

    private static void EventRewards(Mutation m, BiancaTheatreEventTable row, int rootUid, List<int> inner)
    {
        int type = row.StepRewardItemType ?? 0;
        if (type == 0) return;
        if (type == 5)
        {
            m.Data.IsOpenVision = 1;
            m.Push(new NotifyBiancaTheatreVisionCondition());
            return;
        }
        NodeRequire(row.StepRewardItemId.Count == row.StepRewardItemCount.Count, "Malformed event rewards");
        for (int i = 0; i < row.StepRewardItemId.Count; i++)
        {
            int id = row.StepRewardItemId[i], count = row.StepRewardItemCount[i];
            NodeRequire(count >= 0, "Negative event reward count");
            switch (type)
            {
                case 1: NodeGoods(m, id, count); break;
                case 2:
                    for (int j = 0; j < count; j++) { GrantInnerItem(m, id); inner.Add(id); }
                    break;
                case 3:
                    for (int j = 0; j < count; j++) QueueNodeChoice(m, new BiancaTheatreStep { StepType = 2, RootUid = rootUid, ItemIds = DrawItemBox(m, id) });
                    break;
                case 4:
                case 6:
                    for (int j = 0; j < count; j++) AppendRecruitStep(m, id, rootUid, type == 6);
                    break;
                case 7:
                    GrantNodeVision(m, id, count);
                    break;
                default: throw new ServerCodeException($"Unsupported event reward type {type}", 1);
            }
        }
    }

    private static void GrantNodeVision(Mutation m, int id, int count)
    {
        var vision = Rows<BiancaTheatreVisionChangeTable>().Single(x => x.Id == id);
        int change = checked(vision.Change * count);
        int max = Rows<BiancaTheatreVisionTable>().Max(x => x.Max);
        long before = m.Balance(NodeVision);
        long after = Math.Clamp(before + change, 0, max);
        if (after > before) m.AddGoods(NodeVision, checked((int)(after - before)));
        else if (after < before) m.AddCost(NodeVision, checked((int)(before - after)));
        m.Push(new NotifyBiancaTheatreVisionChange { VisionId = id });
    }

    private static void QueueNodeChoice(Mutation m, BiancaTheatreStep step)
    {
        var current = CurrentStep(m);
        if (m.State.QueuedSteps.Count != 0 || (step.RootUid != 0 && current?.RootUid == step.RootUid && current.Overdue == 0))
            m.State.QueuedSteps.Add(step);
        else AppendStep(m, step);
    }

    private static void SetEventStep(Mutation m, BiancaTheatreStep parent, BiancaTheatreNodeSlot slot, int next)
    {
        slot.CurStepId = next;
        slot.FightTemplateId = 0;
        if (next == 0) return;
        var row = Rows<BiancaTheatreEventTable>().Single(x => x.EventId == slot.EventId && x.StepId == next);
        if (row.Type is 5 or 7)
        {
            var fight = Rows<BiancaTheatreFightNodeTable>().Single(x => x.Id == row.FightNodeId);
            slot.FightTemplateId = PickWeighted(Rows<BiancaTheatreFightStageTemplateTable>().Where(x => x.TemplateId == fight.TemplateId), x => x.Weight).Id;
            ArmNodeFight(m, parent, slot, row.NextStepId ?? 0);
        }
    }

    private static void ArmNodeFight(Mutation m, BiancaTheatreStep parent, BiancaTheatreNodeSlot slot, int eventNext = 0)
    {
        var template = Rows<BiancaTheatreFightStageTemplateTable>().Single(x => x.Id == slot.FightTemplateId);
        m.State.Fight = new BiancaTheatreFightState
        {
            StageId = checked((uint)template.StageId), FightTemplateId = template.Id,
            NodeId = parent.NodeData!.NodeId, SlotId = slot.SlotId,
            EventId = slot.EventId, EventStepId = slot.CurStepId, EventNextStepId = eventNext
        };
    }

    public static void CompleteEventFight(Mutation m)
    {
        var fight = m.State.Fight ?? throw new ServerCodeException("No pending event fight", 1);
        var parent = NodeCurrent(m, 5);
        var slot = SelectedNode(m);
        NodeRequire(slot.EventId == fight.EventId && slot.CurStepId == fight.EventStepId, "Event fight is stale");
        var row = Rows<BiancaTheatreEventTable>().Single(x => x.EventId == slot.EventId && x.StepId == slot.CurStepId);
        EventRewards(m, row, parent.Uid, []);
        var eventFight = Rows<BiancaTheatreFightNodeTable>().Single(x => x.Id == row.FightNodeId);
        if (eventFight.GoldId != 0)
            NodeGoods(m, NodeCoin, Rows<BiancaTheatreGoldTable>().Single(x => x.Id == eventFight.GoldId).Count);
        if (eventFight.ItemBoxId is > 0) AppendItemBox(m, eventFight.ItemBoxId.Value, parent.Uid);
        var effects = SystemEffects(m);
        int artifacts = effects.Where(x => x.Type == 21).Sum(x => checked((int)x.Params[0]))
            + (eventFight.Difficulty == 2 ? effects.Count(x => x.Type == 16) : 0);
        for (int i = 0; i < artifacts; i++) AppendItemBox(m, DrawChapterBonusArtifactBox(m), parent.Uid);
        slot.PassedStepId.Add(row.StepId);
        RecordEventChoice(m, row.EventId, row.StepId);
        m.State.Fight = null;
        SetEventStep(m, parent, slot, fight.EventNextStepId);
        m.Push(new NotifyBiancaTheatreNodeNextStep { EventId = slot.EventId, NextStepId = fight.EventNextStepId });
        if (fight.EventNextStepId == 0 && CurrentStep(m)?.Uid == parent.Uid) CompleteNode(m);
    }

    private static List<BiancaTheatreShopItem> GenerateShop(Mutation m, int shopId)
    {
        var shop = Rows<BiancaTheatreNodeShopTable>().Single(x => x.Id == shopId);
        var result = new List<BiancaTheatreShopItem>();
        for (int index = 0; index < shop.GroupId.Count; index++)
        {
            var pool = Rows<BiancaTheatreNodeShopItemTable>().Where(x => x.GroupId == shop.GroupId[index]
                && !result.Any(y => y.ItemType == x.ItemType && y.ItemId == (x.ItemId ?? 0) && y.TicketId == (x.TicketId ?? 0))
                && (x.ItemType != 1 || Rows<BiancaTheatreItemTable>().Any(i => i.Id == x.ItemId
                    && (m.Data.UnlockItemId.Contains(i.Id) || Condition(m, i.UnlockConditionId ?? 0))))).ToList();
            var item = new BiancaTheatreShopItem { Uid = Uid(m) };
            if (pool.Count == 0) item.IsLock = 2;
            else
            {
                var source = PickWeighted(pool, x => x.Weight);
                NodeRequire(source.ItemType is 1 or 2, "Unknown shop item type");
                item.ItemType = source.ItemType;
                item.ItemId = source.ItemId ?? 0;
                item.TicketId = source.TicketId ?? 0;
                item.Price = source.Price;
                item.DiscountPrice = source.Price;
                item.IsLock = Condition(m, NodeArray(shop.ConditionId, index)) ? 0 : 1;
            }
            result.Add(item);
        }
        int count = shop.DiscountNum + SystemEffects(m).Where(x => x.Type == 7).Sum(x => checked((int)x.Params[0]));
        var candidates = result.Where(x => x.IsLock != 2).ToList();
        for (int i = 0; i < count && candidates.Count > 0; i++)
        {
            var chosen = PickWeighted(candidates, _ => 1);
            candidates.Remove(chosen);
            double price = chosen.Price * shop.DiscountRate / 100.0;
            foreach (var effect in SystemEffects(m).Where(x => x.Type == 11)) price *= effect.Params[0];
            // Approved private-server policy: round nonnegative discounted costs upward.
            chosen.DiscountPrice = checked((int)Math.Ceiling(Math.Max(0, price)));
        }
        return result;
    }

    [RequestPacketHandler("BiancaTheatreNodeShopBuyItemRequest")]
    public static void BiancaTheatreNodeShopBuyItemRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreNodeShopBuyItemRequest, BiancaTheatreNodeShopBuyItemResponse>(session, packet, (m, req, res) =>
        {
            var parent = NodeCurrent(m, 5);
            var slot = SelectedNode(m);
            NodeRequire(slot.SlotType == 3, "Selected node is not a shop");
            var item = slot.ShopItems.SingleOrDefault(x => x.Uid == req.ShopItemUid);
            NodeRequire(item != null && item.IsLock == 0 && item.IsBuy == 0, "Shop item is unavailable");
            NodeRequire(m.Balance(NodeCoin) >= item!.DiscountPrice, "Insufficient shop currency");
            if (item.DiscountPrice > 0)
            {
                m.AddCost(NodeCoin, item.DiscountPrice);
                m.State.TotalCookieSpent = checked(m.State.TotalCookieSpent + item.DiscountPrice);
            }
            item.IsBuy = 1;
            switch (item.ItemType)
            {
                case 1: GrantInnerItem(m, item.ItemId); break;
                case 2: AppendRecruitStep(m, item.TicketId, parent.Uid); break;
                default: throw new ServerCodeException("Unknown shop item type", 1);
            }
        });

    [RequestPacketHandler("BiancaTheatreEndNodeRequest")]
    public static void BiancaTheatreEndNodeRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreEmptyRequest, BiancaTheatreEndNodeResponse>(session, packet, (m, req, res) =>
        {
            NodeRequire(SelectedNode(m).SlotType == 3 && m.State.Fight == null, "Only the current shop can be ended");
            CompleteNode(m);
        });

    private static BiancaTheatreFightReward NodeReward(Mutation m, int type, int configId, int tag = 0) =>
        new() { Uid = Uid(m), RewardType = type, ConfigId = configId, Count = 1, TagType = tag };

    public static void AppendFightRewards(Mutation m, BiancaTheatreNodeSlot slot)
    {
        var parent = NodeCurrent(m, 5);
        NodeRequire(parent.NodeData!.Slots.Contains(slot) && slot.Selected != 0 && slot.SlotType == 1, "Fight reward node is not selected");
        var rewards = slot.NodeRewards;
        AppendStep(m, new BiancaTheatreStep { StepType = 6, RootUid = parent.Uid, FightRewards = rewards });
    }

    [RequestPacketHandler("BiancaTheatreRecvFightRewardRequest")]
    public static void BiancaTheatreRecvFightRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreRecvFightRewardRequest, BiancaTheatreRecvFightRewardResponse>(session, packet, (m, req, res) =>
        {
            var step = NodeCurrent(m, 6);
            var reward = step.FightRewards.SingleOrDefault(x => x.Uid == req.Uid);
            NodeRequire(reward != null && reward.Received == 0, "Fight reward is not available");
            reward!.Received = 1;
            int start = m.Goods.Count;
            NodeRequire(reward.Count > 0, "Invalid fight reward count");
            for (int i = 0; i < reward.Count; i++)
            {
                switch (reward.RewardType)
                {
                    case 1: AppendItemBox(m, reward.ConfigId, step.Uid); break;
                    case 2: AppendRecruitStep(m, reward.ConfigId, step.Uid); break;
                    case 3:
                        var gold = Rows<BiancaTheatreGoldTable>().Single(x => x.Id == reward.ConfigId);
                        NodeGoods(m, NodeCoin, gold.Count);
                        break;
                    default: throw new ServerCodeException($"Unknown fight reward type {reward.RewardType}", 1);
                }
            }
            res.RewardGoodsList = NodeGoodsResponse(m, start);
        });

    [RequestPacketHandler("BiancaTheatreEndRecvFightRewardRequest")]
    public static void BiancaTheatreEndRecvFightRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreEmptyRequest, BiancaTheatreEndRecvFightRewardResponse>(session, packet, (m, req, res) =>
        {
            var step = NodeCurrent(m, 6);
            NodeRequire(step.FightRewards.All(x => x.Received != 0), "Fight rewards remain unclaimed");
            step.Overdue = 1;
            var parent = m.Data.CurChapterDb!.Steps.Single(x => x.Uid == step.RootUid);
            parent.Overdue = 0;
            CompleteNode(m);
        });

    private static List<RewardGoods> NodeGoodsResponse(Mutation m, int start) => m.Goods.Skip(start).Select(row => new RewardGoods
    {
        Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
        RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidOperationException("Unsupported theatre goods"))
    }).ToList();

    public static void AppendNodeStep(Mutation m)
    {
        NodeRequire(m.Data.CurChapterDb != null && m.Data.CurTeamId != 0, "No active adventure");
        var chapter = m.Data.CurChapterDb!;
        var current = CurrentStep(m);
        if (current != null && current.StepType == 5) return;
        NodeRequire(current == null || current.Overdue != 0, "A theatre choice is still pending");
        var rows = Rows<BiancaTheatreNodeTable>().Where(x => x.ChapterId == chapter.ChapterId).OrderBy(x => x.Id).ToList();
        NodeRequire(chapter.PassNodeCount < rows.Count, "Chapter progression has no remaining node");
        var source = rows[chapter.PassNodeCount];
        int count = PickWeighted(Enumerable.Range(1, source.CountWeight.Count), n => source.CountWeight[n - 1]);
        int[] floors = [source.FightFloor ?? 0, source.EventFloor ?? 0, source.ShopFloor ?? 0];
        int[] tops = [source.FightTop ?? 0, source.EventTop ?? 0, source.ShopTop ?? 0];
        int[] weights = [source.FightWeight ?? 0, source.EventWeight ?? 0, source.ShopWeight ?? 0];
        NodeRequire(floors.Sum() <= count && tops.Sum() >= count, "Contradictory node slot limits");
        var types = new List<int>();
        for (int type = 0; type < floors.Length; type++)
            for (int n = 0; n < floors[type]; n++) types.Add(type + 1);
        while (types.Count < count)
            types.Add(PickWeighted(Enumerable.Range(1, 3).Where(t => types.Count(x => x == t) < tops[t - 1]), t => weights[t - 1]));
        var node = new BiancaTheatreNodeData { NodeId = source.Id };
        var history = NodeHistory(m).Where(x => x.NodeData != null).SelectMany(x => x.NodeData!.Slots).Where(x => x.Selected != 0).ToList();
        var chapterSource = Rows<BiancaTheatreChapterTable>().Single(x => x.Id == chapter.ChapterId);
        foreach (int type in types)
        {
            var slot = new BiancaTheatreNodeSlot { SlotId = node.Slots.Count + 1, SlotType = type };
            switch (type)
            {
                case 1:
                    var fights = Rows<BiancaTheatreFightLibTable>().Where(x => Condition(m, x.ConditionId ?? 0)
                        && (x.GameRepeatable != 0 || !history.Any(h => h.FightId == x.FightId))).ToList();
                    var fightPool = fights.Where(x => x.GroupId == source.FightLibId).ToList();
                    // Approved private-server policy: floor libraries are exhaustion fallbacks.
                    if (fightPool.Count == 0) fightPool = fights.Where(x => x.GroupId == chapterSource.FloorFightGroupId).ToList();
                    var fight = PickWeighted(fightPool, x => x.Weight);
                    var fightNode = Rows<BiancaTheatreFightNodeTable>().Single(x => x.Id == fight.FightId);
                    slot.FightId = fightNode.Id;
                    slot.FightTemplateId = PickWeighted(Rows<BiancaTheatreFightStageTemplateTable>().Where(x => x.TemplateId == fightNode.TemplateId), x => x.Weight).Id;
                    slot.NodeRewards = GenerateNodeRewards(m, source, fightNode);
                    break;
                case 2:
                    var events = Rows<BiancaTheatreEventGroupTable>().Where(x => Condition(m, x.ConditionId ?? 0)
                        && ((x.GameRepeatable ?? 0) != 0 || !history.Any(h => h.EventId == x.EventId))
                        && ((x.NodeRepeatable ?? 0) != 0 || !node.Slots.Any(h => h.EventId == x.EventId))).ToList();
                    var eventPool = events.Where(x => x.GroupId == source.EventLibId).ToList();
                    if (eventPool.Count == 0) eventPool = events.Where(x => x.GroupId == chapterSource.FloorEventGroupId).ToList();
                    var ev = PickWeighted(eventPool, x =>
                        {
                            double weight = x.Weight;
                            foreach (var effect in SystemEffects(m).Where(e => e.Type == 17 && (int)e.Params[0] == x.EventId))
                                weight *= effect.Params[1];
                            return checked((int)Math.Round(weight));
                        });
                    slot.EventId = ev.EventId;
                    slot.CurStepId = Rows<BiancaTheatreEventTable>().Where(x => x.EventId == ev.EventId).Min(x => x.StepId);
                    var first = Rows<BiancaTheatreEventTable>().Single(x => x.EventId == ev.EventId && x.StepId == slot.CurStepId);
                    if (first.Type is 5 or 7)
                    {
                        var eventFight = Rows<BiancaTheatreFightNodeTable>().Single(x => x.Id == first.FightNodeId);
                        slot.FightTemplateId = PickWeighted(Rows<BiancaTheatreFightStageTemplateTable>().Where(x => x.TemplateId == eventFight.TemplateId), x => x.Weight).Id;
                    }
                    break;
                case 3:
                    var shops = Rows<BiancaTheatreNodeShopGroupTable>().Where(x => x.GameRepeatable != 0 || !history.Any(h => h.ShopId == x.ShopId)).ToList();
                    var shopPool = shops.Where(x => x.GroupId == source.ShopLibId).ToList();
                    if (shopPool.Count == 0) shopPool = shops.Where(x => x.GroupId == chapterSource.FloorShopGroupId).ToList();
                    var shop = PickWeighted(shopPool, x => x.Weight);
                    slot.ShopId = shop.ShopId;
                    slot.ShopItems = GenerateShop(m, shop.ShopId);
                    break;
            }
            node.Slots.Add(slot);
        }
        AppendStep(m, new BiancaTheatreStep { StepType = 5, NodeData = node });
    }

    private static List<BiancaTheatreFightReward> GenerateNodeRewards(Mutation m, BiancaTheatreNodeTable node, BiancaTheatreFightNodeTable fight)
    {
        var effects = SystemEffects(m);
        var result = new List<BiancaTheatreFightReward>();
        if (fight.GoldId != 0) result.Add(NodeReward(m, 3, fight.GoldId));
        if (fight.ItemBoxId is > 0) result.Add(NodeReward(m, 1, fight.ItemBoxId.Value, 1));
        for (int index = 0; index < node.RewardProbability.Count; index++)
        {
            int sourceProbability = node.RewardProbability[index];
            NodeRequire(sourceProbability is >= 0 and <= 100, "Invalid source reward probability");
            // Approved private-server policy: modifier values add basis points, not relative percentages.
            int probability = Math.Clamp(checked(sourceProbability * 100 + (index > 0
                ? effects.Where(x => x.Type == 18).Sum(x => checked((int)x.Params[0])) : 0)), 0, 10000);
            if (!PickWeighted(new[] { false, true }, yes => yes ? probability : 10000 - probability)) continue;
            int type = PickWeighted(new[] { 1, 2, 3 }, t => t switch
            {
                1 => NodeArray(node.ItemBoxWeight, index),
                2 => NodeArray(node.RecruitTicketWeight, index),
                _ => NodeArray(node.GoldWeight, index)
            });
            int config = type switch
            {
                1 => PickWeighted(Rows<BiancaTheatreItemBoxGroupTable>().Where(x => x.GroupId == NodeArray(node.ItemBoxGroupId, index)), x => x.Weight).ItemBoxId,
                2 => PickWeighted(Rows<BiancaTheatreRecruitTicketGroupTable>().Where(x => x.GroupId == NodeArray(node.RecruitTicketGroupId, index)
                    && !result.Any(r => r.RewardType == 2 && r.ConfigId == x.TicketId)), x => x.Weight).TicketId,
                _ => PickWeighted(Rows<BiancaTheatreGoldGroupTable>().Where(x => x.GroupId == NodeArray(node.GoldGroupId, index)), x => x.Weight).GoldId
            };
            result.Add(NodeReward(m, type, config, index == 0 ? 0 : 2));
        }
        int ticketChance = Math.Clamp(effects.Where(x => x.Type == 19).Sum(x => checked((int)x.Params[0])), 0, 10000);
        if (ticketChance > 0 && PickWeighted(new[] { false, true }, yes => yes ? ticketChance : 10000 - ticketChance))
        {
            var tickets = Rows<BiancaTheatreRecruitTicketGroupTable>().Where(x => node.RecruitTicketGroupId.Contains(x.GroupId)
                && !result.Any(r => r.RewardType == 2 && r.ConfigId == x.TicketId)).ToList();
            result.Add(NodeReward(m, 2, PickWeighted(tickets, x => x.Weight).TicketId, 3));
        }
        int artifacts = effects.Where(x => x.Type == 21).Sum(x => checked((int)x.Params[0]))
            + (fight.Difficulty == 2 ? effects.Count(x => x.Type == 16) : 0);
        for (int i = 0; i < artifacts; i++) result.Add(NodeReward(m, 1, DrawChapterBonusArtifactBox(m), 3));
        return result;
    }

    private static int DrawChapterBonusArtifactBox(Mutation m)
    {
        // Approved private-server policy: bonus artifacts use this chapter's authored reward pools.
        var nodes = Rows<BiancaTheatreNodeTable>().Where(x => x.ChapterId == m.Data.CurChapterId).ToList();
        var weights = new Dictionary<int, int>();
        var groups = Rows<BiancaTheatreItemBoxGroupTable>();
        foreach (var node in nodes)
            for (int index = 0; index < node.ItemBoxGroupId.Count; index++)
                if (NodeArray(node.ItemBoxWeight, index) > 0)
                    foreach (var entry in groups.Where(x => x.GroupId == node.ItemBoxGroupId[index]))
                        weights[entry.ItemBoxId] = checked(weights.GetValueOrDefault(entry.ItemBoxId) + entry.Weight);
        var chapter = Rows<BiancaTheatreChapterTable>().Single(x => x.Id == m.Data.CurChapterId);
        var libraries = nodes.Select(x => x.FightLibId).Append(chapter.FloorFightGroupId).ToHashSet();
        var used = NodeHistory(m).Where(x => x.NodeData != null).SelectMany(x => x.NodeData!.Slots)
            .Where(x => x.Selected != 0).Select(x => x.FightId).ToHashSet();
        var fightIds = Rows<BiancaTheatreFightLibTable>().Where(x => libraries.Contains(x.GroupId)
            && Condition(m, x.ConditionId ?? 0) && (x.GameRepeatable != 0 || !used.Contains(x.FightId))).Select(x => x.FightId).ToHashSet();
        var fixedBoxes = Rows<BiancaTheatreFightNodeTable>().Where(x => fightIds.Contains(x.Id) && x.ItemBoxId is > 0)
            .Select(x => x.ItemBoxId!.Value).Distinct().ToList();
        foreach (int box in fixedBoxes) weights.TryAdd(box, 1);
        return PickWeighted(weights, x => x.Value).Key;
    }

    [RequestPacketHandler("BiancaTheatreSelectNodeRequest")]
    public static void BiancaTheatreSelectNodeRequestHandler(Session session, Packet.Request packet) =>
        Handle<BiancaTheatreSelectNodeRequest, BiancaTheatreSelectNodeResponse>(session, packet, (m, req, res) =>
        {
            var step = NodeCurrent(m, 5);
            NodeRequire(step.NodeData?.NodeId == req.NodeId, "Node is not reachable");
            var slot = step.NodeData!.Slots.SingleOrDefault(x => x.SlotId == req.SlotId);
            NodeRequire(slot != null && !step.NodeData.Slots.Any(x => x.Selected != 0), "Node slot is unavailable");
            slot!.Selected = 1;
            if (slot.SlotType == 1) ArmNodeFight(m, step, slot);
            if (slot.SlotType == 2 && slot.FightTemplateId != 0)
            {
                var ev = Rows<BiancaTheatreEventTable>().Single(x => x.EventId == slot.EventId && x.StepId == slot.CurStepId);
                ArmNodeFight(m, step, slot, ev.NextStepId ?? 0);
            }
        });

    public static void CompleteNode(Mutation m)
    {
        var step = NodeCurrent(m, 5);
        var slot = SelectedNode(m);
        NodeRequire(m.State.Fight == null, "Node fight is not complete");
        var chapter = m.Data.CurChapterDb!;
        step.Overdue = 1;
        chapter.PassNodeCount++;
        m.State.RunNodeCount++;
        m.Data.GamePassNodeCount++;
        if (slot.SlotType == 1 || slot.PassedStepId.Any(id => Rows<BiancaTheatreEventTable>().Any(x => x.EventId == slot.EventId && x.StepId == id && x.Type is 5 or 7)))
        {
            chapter.PassFightCount++;
            m.State.RunFightNodeCount++;
            m.Data.HistoryTotalPassFightNodeCount++;
            m.Push(new NotifyBiancaTheatreFightNodeCountChange { FightNodeCount = m.State.RunFightNodeCount });
        }
        foreach (var effect in SystemEffects(m).Where(x => x.Type == 4))
            NodeGoods(m, checked((int)effect.Params[0]), checked((int)effect.Params[1]));
        if (chapter.PassNodeCount < Rows<BiancaTheatreNodeTable>().Count(x => x.ChapterId == chapter.ChapterId))
        {
            ContinueRun(m);
            return;
        }
        chapter.PassChapter = 1;
        if (!m.State.RunPassChapterIds.Contains(chapter.ChapterId)) m.State.RunPassChapterIds.Add(chapter.ChapterId);
        if (!m.Data.PassChapterIds.Contains(chapter.ChapterId)) m.Data.PassChapterIds.Add(chapter.ChapterId);
        var source = Rows<BiancaTheatreChapterTable>().Single(x => x.Id == chapter.ChapterId);
        var next = Rows<BiancaTheatreChapterTable>().Where(x => source.NextChapterIds.Contains(x.Id) && Condition(m, x.ConditionId ?? 0)).ToList();
        NodeRequire(next.Count <= 1, "Ambiguous chapter continuation");
        if (next.Count == 0)
        {
            m.Push(new NotifyBiancaTheatreAdventureSettle { SettleData = SettleRun(m, false) });
            return;
        }
        m.State.CompletedChapters.Add(chapter);
        m.Data.CurChapterId = next[0].Id;
        m.Data.CurChapterDb = new BiancaTheatreChapterDb { ChapterId = next[0].Id };
        m.State.ReachedChapterIds.Add(next[0].Id);
        BeginChapter(m);
    }
}
