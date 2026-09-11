using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using AscNet.Table.V2.share.theatre3;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    private static List<T> NodeRows<T>() where T : ITable => TableReaderV2.Parse<T>();

    // Documented local generation: nonnegative weights, uniform only for all-zero
    // authored pools. Slot multiplicity may repeat eligible encounters; selection
    // consumes game-repeat eligibility. Missing/exhausted pools remain errors.
    internal static T Weighted<T>(IEnumerable<T> source, Func<T, int> weight)
    {
        var rows = source.ToList();
        Require(rows.Count > 0, 20203018);
        long total = rows.Sum(row => (long)Math.Max(0, weight(row)));
        if (total == 0) return rows[Random.Shared.Next(rows.Count)];
        long draw = Random.Shared.NextInt64(total);
        foreach (T row in rows)
        {
            draw -= Math.Max(0, weight(row));
            if (draw < 0) return row;
        }
        throw new InvalidOperationException("Theatre3 weighted selection overflow.");
    }

    internal static Theatre3Step CurrentStep(Mutation m, params int[] allowedTypes)
    {
        Require(m.Data.CurChapterDb != null, 20203015);
        var pending = m.Data.CurChapterDb!.Steps.Where(s => s.Overdue == 0).ToList();
        var step = pending.LastOrDefault(s => s.StepType is 6 or 7) ?? pending.LastOrDefault();
        Require(step != null, 20203025);
        Require(allowedTypes.Length == 0 || allowedTypes.Contains(step!.StepType), 20203019);
        return step!;
    }

    private static Theatre3Step NodeStep(Mutation m)
    {
        var step = m.Data.CurChapterDb?.Steps.LastOrDefault(s => s.StepType == 2 && s.Overdue == 0);
        Require(step != null, 20203020);
        return step!;
    }

    private static Theatre3NodeData ActiveNode(Mutation m, Theatre3Step step)
    {
        var node = new[] { step.NodeData, step.ConnectNodeData }.FirstOrDefault(n => n?.ChapterId == m.Data.CurChapterId);
        Require(node != null, 20203020);
        return node!;
    }

    internal static Theatre3NodeSlot CurrentSlot(Mutation m)
    {
        var slot = ActiveNode(m, NodeStep(m)).Slots.SingleOrDefault(s => s.Selected != 0);
        Require(slot != null, 20203021);
        return slot!;
    }

    internal static void PushStep(Mutation m, Theatre3Step step, int rootUid = 0)
    {
        Require(m.Data.CurChapterDb != null, 20203015);
        if (step.Uid == 0) step.Uid = AllocateUid(m);
        if (rootUid == 0 && step.StepType != 2)
        {
            var parent = m.Data.CurChapterDb!.Steps.LastOrDefault(s => s.Overdue == 0);
            rootUid = parent?.RootUid > 0 ? parent.RootUid : parent?.Uid ?? 0;
        }
        step.RootUid = rootUid;
        m.Data.CurChapterDb!.Steps.Add(step);
        PublishStep(m, step);
    }

    private static void PublishStep(Mutation m, Theatre3Step step) =>
        m.Push(new NotifyTheatre3AddStep { ChapterId = m.Data.CurChapterId, Step = Clone(step) });

    internal static void CompleteStep(Mutation m) => FinishStep(m, CurrentStep(m));

    internal static void FinishStep(Mutation m, Theatre3Step step)
    {
        Require(step.Overdue == 0, 20203054);
        bool foregroundChanged = CurrentStep(m).Uid == step.Uid;
        step.Overdue = 1;
        bool reconciled = false;
        if (foregroundChanged)
        {
            while (m.Data.CurChapterDb!.Steps.Any(s => s.Overdue == 0))
            {
                var next = CurrentStep(m);
                if (next.StepType is not (4 or 6 or 7)) break;
                reconciled |= ReconcileEquipmentStep(m, next);
                if (next.Overdue == 0) break;
            }
        }
        ResumeNode(m);
        if ((step.StepType == 9 || reconciled) && m.Data.DifficultyId != 0) PublishNodeSnapshot(m);
    }

    // AddStep explicitly ignores an existing UID in the client. Only transitions
    // with no consumed delta (prop completion, reconciled offers, chained templates)
    // use this replacement; coalesce preceding mode deltas to prevent double adds.
    private static void PublishNodeSnapshot(Mutation m)
    {
        m.Pushes.RemoveAll(p => p.Name.StartsWith("NotifyTheatre3", StringComparison.Ordinal)
            && p.Name != nameof(NotifyTheatre3AdventureSettle));
        m.Push(BsonSerializer.Deserialize<NotifyTheatre3ActivityData>(m.Data.ToBson()));
    }

    private static void ResumeNode(Mutation m)
    {
        if (m.Data.CurChapterDb == null || m.Data.DifficultyId == 0) return;
        if (!m.Data.CurChapterDb.Steps.Any(s => s.Overdue == 0)) { GenerateNextNode(m); return; }
        var current = CurrentStep(m);
        if (current.StepType != 2) return;
        var slot = ActiveNode(m, current).Slots.SingleOrDefault(s => s.Selected != 0);
        if ((slot?.SlotType == 2 && slot.CurStepId == 0)
            || m.State.AppliedEffectTriggers.Contains($"node-complete:{current.Uid}")) CompleteNode(m);
    }

    internal static void BeginAdventure(Mutation m)
    {
        m.State.ContentUseCounts.Clear(); m.State.EncounterUseCounts.Clear(); m.State.SlotContentIds.Clear();
        m.State.EventStepEnterCounts.Clear(); m.State.ActiveLinkGroups.Clear(); m.State.CompletedChapterIds.Clear();
        m.State.RunPassedNodeIds.Clear(); m.State.RunPassedEventStepIds.Clear(); m.State.RunItemsObtainedByQuality.Clear();
        m.State.RunEventIds.Clear(); m.State.RunFightNodeIds.Clear(); m.State.RunCharacterIds.Clear();
        m.State.RunNodeCount = m.State.RunFightCount = m.State.RunChapterCount = 0;
        m.State.RunMaxItemCount = m.State.RunMaxEquipCount = m.State.RunMaxSuitCount = 0;
        m.State.RunNewItemIds.Clear(); m.State.RunNewEquipIds.Clear();
        m.State.PreviousEquipSuitIds = m.State.EligibleEquipSuitIds.ToList();
        m.State.EligibleEquipSuitIds.Clear();
        m.State.Fight = null; m.State.LastFightSettle = null;
        m.State.LastFightId = 0; m.State.LastFightStageId = 0; m.State.PendingEndingId = 0;
        var difficulty = NodeRows<Theatre3DifficultyTable>().Single(x => x.Id == m.Data.DifficultyId);
        Require(NodeRows<Theatre3ChapterGroupTable>().Any(x => x.Id == difficulty.ChapterGroupId), 20203004);
        var chapters = NodeRows<Theatre3ChapterTable>().Where(x => x.GroupId == difficulty.ChapterGroupId).ToList();
        var targets = chapters.SelectMany(x => x.NextChapterIds).ToHashSet();
        var starts = chapters.Where(x => x.ChapterType == 1 && !targets.Contains(x.Id) && IsConditionSatisfied(m, x.ConditionId ?? 0)).ToList();
        Require(starts.Count > 0, 20203004);
        StartChapter(m, Weighted(starts, _ => 1));
        foreach (var step in CreateInitialSteps(m).AsEnumerable().Reverse()) PushStep(m, step);
    }

    private static void StartChapter(Mutation m, Theatre3ChapterTable chapter)
    {
        m.Data.CurChapterId = chapter.Id;
        int pairedChapter = chapter.ConnectChapter is > 0 ? chapter.ConnectChapter.Value
            : NodeRows<Theatre3ChapterTable>().SingleOrDefault(x => x.ConnectChapter == chapter.Id)?.Id ?? 0;
        m.Data.CurChapterDb = new() { ChapterId = chapter.Id, ConnectChapterId = pairedChapter };
        m.Push(new NotifyTheatre3AddChapter { Chapter = Clone(m.Data.CurChapterDb) });
        RecordProgress(m, 104004, 1, chapter.Id);
        GenerateNextNode(m);
    }

    private static void GenerateNextNode(Mutation m)
    {
        var db = m.Data.CurChapterDb!;
        var nodes = NodeRows<Theatre3NodeTable>().Where(x => x.ChapterId == m.Data.CurChapterId
            && !db.PassNodeIds.Contains(x.Id) && IsConditionSatisfied(m, x.ConditionId ?? 0)).OrderBy(x => x.Id).ToList();
        if (nodes.Count == 0)
        {
            var chapter = NodeRows<Theatre3ChapterTable>().Single(x => x.Id == m.Data.CurChapterId);
            db.PassChapter = 1;
            m.State.RunChapterCount++;
            m.State.CompletedChapterIds.Add(chapter.Id);
            if (!m.Data.PassChapterIds.Contains(m.State.RunChapterCount)) m.Data.PassChapterIds.Add(m.State.RunChapterCount);
            var next = chapter.NextChapterIds.Where(id => id > 0).Select(id => NodeRows<Theatre3ChapterTable>().Single(x => x.Id == id))
                .Where(x => IsConditionSatisfied(m, x.ConditionId ?? 0)).ToList();
            if (next.Count == 0) { SettleRun(m); return; }
            StartChapter(m, Weighted(next, _ => 1));
            return;
        }
        var source = nodes[0];
        var step = new Theatre3Step { StepType = 2, NodeData = GenerateNode(m, source) };
        var paired = source.ConnectNode is > 0
            ? NodeRows<Theatre3NodeTable>().Single(x => x.Id == source.ConnectNode)
            : NodeRows<Theatre3NodeTable>().SingleOrDefault(x => x.ConnectNode == source.Id);
        if (paired != null && IsConditionSatisfied(m, paired.ConditionId ?? 0))
            step.ConnectNodeData = GenerateNode(m, paired);
        PushStep(m, step);
        ApplyEffectTrigger(m, Theatre3EffectTrigger.EquipmentChanged);
    }

    private static int EncounterKey(int type, int id) => checked(type * 1000000 + id);
    private static bool EncounterAvailable(Mutation m, int type, int id, int repeat) => repeat != 0 || !m.State.EncounterUseCounts.ContainsKey(EncounterKey(type, id));
    private static int LinkWeight(Mutation m, int type, int target) => NodeRows<Theatre3NodeLinkTable>()
        .Where(x => m.State.ActiveLinkGroups.Contains(x.GroupId) && x.LinkType == type && x.LinkTargetId == target).Sum(x => x.LinkAddWeight);

    private static IEnumerable<Theatre3FightLibTable> NodeFightPool(Mutation m, int group) =>
        NodeRows<Theatre3FightLibTable>().Where(x => x.GroupId == group && IsConditionSatisfied(m, x.ConditionId ?? 0)
            && EncounterAvailable(m, 1, x.FightNodeId, x.GameRepeatable));

    private static IEnumerable<Theatre3EventGroupTable> NodeEventPool(Mutation m, int group) =>
        NodeRows<Theatre3EventGroupTable>().Where(x => x.GroupId == group && IsConditionSatisfied(m, x.ConditionId ?? 0)
            && EncounterAvailable(m, 2, x.EventId, x.GameRepeatable ?? 0));

    private static IEnumerable<Theatre3NodeShopGroupTable> NodeShopPool(Mutation m, int group) =>
        NodeRows<Theatre3NodeShopGroupTable>().Where(x => x.GroupId == group && IsConditionSatisfied(m, x.ConditionId ?? 0)
            && EncounterAvailable(m, 3, x.ShopId, x.GameRepeatable));

    private static bool ContentAvailable(Mutation m, Theatre3ContentLibTable content) => content.UpperLimit > 0
        && IsConditionSatisfied(m, content.ConditionId ?? 0) && (content.Type switch
        {
            1 => NodeFightPool(m, content.LibId).Any(),
            2 => NodeEventPool(m, content.LibId).Any(),
            3 => NodeShopPool(m, content.LibId).Any(),
            _ => throw new InvalidDataException($"Unknown Theatre3 content type {content.Type}.")
        });

    private static Theatre3NodeData GenerateNode(Mutation m, Theatre3NodeTable source)
    {
        int count = Weighted(Enumerable.Range(1, source.CountWeight.Count), n => source.CountWeight[n - 1]);
        var content = NodeRows<Theatre3ContentLibTable>().Where(x => x.GroupId == source.ContentLibId
            && ContentAvailable(m, x)).OrderBy(x => x.Id).ToList();
        Require(content.Count > 0, 20203018);
        var picks = new List<Theatre3ContentLibTable>();
        // Local rule for inconsistent authored constraints: only eligible libraries
        // participate; clamp the drawn count to their feasible lower/upper capacity.
        // Never remove a condition or insert a candidate from a different library.
        foreach (var row in content)
            for (int i = 0; i < Math.Clamp(row.LowerLimit ?? 0, 0, row.UpperLimit); i++) picks.Add(row);
        count = Math.Clamp(count, Math.Max(1, picks.Count), content.Sum(x => x.UpperLimit));
        while (picks.Count < count)
            picks.Add(Weighted(content.Where(x => picks.Count(y => y.Id == x.Id) < x.UpperLimit),
                x => x.Weight + (IsConditionSatisfied(m, x.AddWeightCondition ?? 0) ? x.AddWeight ?? 0 : 0)));
        var node = new Theatre3NodeData { ChapterId = source.ChapterId, NodeId = source.Id };
        foreach (var row in picks)
        {
            var slot = new Theatre3NodeSlot { SlotId = AllocateUid(m), SlotType = row.Type };
            m.State.SlotContentIds.Add(slot.SlotId, row.Id);
            m.State.ContentUseCounts[row.Id] = m.State.ContentUseCounts.GetValueOrDefault(row.Id) + 1;
            switch (row.Type)
            {
                case 1:
                    var fights = NodeFightPool(m, row.LibId);
                    var fight = Weighted(fights, x => m.State.EncounterUseCounts.ContainsKey(EncounterKey(1, x.FightNodeId)) ? x.RepeatWeight ?? x.Weight ?? 0 : x.Weight ?? 0);
                    InitializeFightSlot(m, slot, fight.FightNodeId);
                    slot.NodeRewards.AddRange(GenerateDrops(m, row.DropGroupId ?? 0));
                    break;
                case 2:
                    var events = NodeEventPool(m, row.LibId);
                    var ev = Weighted(events, x => (x.Weight ?? 0) + LinkWeight(m, 2, x.EventId)
                        + x.AddWeight.Select((w, i) => i < x.AddWeightCondition.Count && IsConditionSatisfied(m, x.AddWeightCondition[i]) ? w : 0).Sum());
                    slot.EventId = ev.EventId; slot.LinkGroupId = ev.LinkGroupId ?? 0;
                    slot.CurStepId = NodeRows<Theatre3EventTable>().Where(x => x.EventId == ev.EventId).Min(x => x.StepId);
                    var first = NodeRows<Theatre3EventTable>().Single(x => x.EventId == slot.EventId && x.StepId == slot.CurStepId);
                    if (first.Type == 4) InitializeFightSlot(m, slot, first.FightNodeId ?? 0);
                    break;
                case 3:
                    var shops = NodeShopPool(m, row.LibId);
                    slot.ShopId = Weighted(shops, x => x.Weight ?? 0).ShopId;
                    GenerateShopItems(m, slot);
                    break;
                default: throw new InvalidDataException($"Unknown Theatre3 content type {row.Type}.");
            }
            node.Slots.Add(slot);
        }
        return node;
    }

    internal static void InitializeFightSlot(Mutation m, Theatre3NodeSlot slot, int fightNodeId)
    {
        var fight = NodeRows<Theatre3FightNodeTable>().Single(x => x.Id == fightNodeId);
        slot.FightId = fight.Id;
        slot.FightTemplateId = Weighted(NodeRows<Theatre3FightStageTemplateTable>().Where(x => x.TemplateId == fight.TemplateId), x => x.Weight).Id;
        slot.NodeRewards = fight.ItemBoxIds.Where(x => x > 0).Select(x => NewReward(m, 1, x))
            .Concat(fight.GoldIds.Where(x => x > 0).Select(x => NewReward(m, 2, x)))
            .Concat(fight.EquipBoxIds.Where(x => x > 0).Select(x => NewReward(m, 3, x))).ToList();
    }

    private static Theatre3NodeReward NewReward(Mutation m, int type, int id, int count = 1, int show = 1, int tag = 0) =>
        new() { Uid = AllocateUid(m), RewardType = type, ConfigId = id, Count = count, IsShow = show, Tag = tag };

    private static List<Theatre3NodeReward> GenerateDrops(Mutation m, int group)
    {
        var rewards = new List<Theatre3NodeReward>();
        foreach (var row in NodeRows<Theatre3DropGroupTable>().Where(x => x.GroupId == group))
        {
            if (Random.Shared.Next(100) >= row.Probability) continue;
            int type = Weighted(new[] { 1, 2, 3 }.Where(t => t switch { 1 => row.ItemBoxGroupId > 0, 2 => row.GoldGroupId > 0, _ => row.EquipBoxGroupId > 0 }),
                t => t switch { 1 => row.ItemBoxWeight ?? 0, 2 => row.GoldWeight ?? 0, _ => row.EquipWeight ?? 0 });
            int lower = type switch { 1 => row.ItemBoxLowerLimit ?? 1, 3 => row.EquipBoxLowerLimit ?? 1, _ => 1 };
            int upper = type switch { 1 => row.ItemBoxUpperLimit ?? lower, 3 => row.EquipBoxUpperLimit ?? lower, _ => row.GoldUpperLimit ?? lower };
            int count = Random.Shared.Next(Math.Max(1, lower), checked(Math.Max(Math.Max(1, lower), upper) + 1));
            int id = type switch
            {
                1 => Weighted(NodeRows<Theatre3ItemBoxTable>().Where(x => x.BoxGroupId == row.ItemBoxGroupId), x => x.Weight).Id,
                2 => SelectGold(m, row.GoldGroupId!.Value),
                _ => Weighted(NodeRows<Theatre3EquipBoxGroupTable>().Where(x => x.GroupId == row.EquipBoxGroupId), x => x.Weight ?? 0).EquipBoxId
            };
            rewards.Add(NewReward(m, type, id, count, row.IsShow ?? 0, row.Tag ?? 0));
        }
        return rewards;
    }

    private static int SelectGold(Mutation m, int group) => Weighted(NodeRows<Theatre3GoldGroupTable>()
        .Where(x => x.GroupId == group && EncounterAvailable(m, 4, x.GoldId, x.GameRepeatable)), x => x.Weight).GoldId;

    internal static void AwardGoldGroup(Mutation m, int groupId, int count = 1) => AwardNodeReward(m, NewReward(m, 2, SelectGold(m, groupId), count));

    internal static void AwardNodeReward(Mutation m, Theatre3NodeReward reward)
    {
        Require(reward.Received == 0, 20203045);
        reward.Received = 1;
        switch (reward.RewardType)
        {
            case 1: OpenItemBox(m, reward.ConfigId, reward.Count); break;
            case 2:
                var gold = NodeRows<Theatre3GoldTable>().Single(x => x.Id == reward.ConfigId);
                // Local provenance rule: authored FightNode.GoldIds receive drop
                // multipliers; GoldGroup boxes are fixed amounts (disjoint sources).
                decimal multiplier = NodeRows<Theatre3FightNodeTable>().Any(x => x.GoldIds.Contains(gold.Id)) ? GetEffects(m).GoldMultiplier : 1m;
                AddCoin(m, checked((int)decimal.Floor(gold.Count * (decimal)reward.Count * multiplier)));
                m.State.EncounterUseCounts[EncounterKey(4, gold.Id)] = m.State.EncounterUseCounts.GetValueOrDefault(EncounterKey(4, gold.Id)) + 1;
                break;
            case 3: OpenEquipBox(m, reward.ConfigId, reward.Count); break;
            default: throw new InvalidDataException($"Unknown Theatre3 reward type {reward.RewardType}.");
        }
    }

    [RequestPacketHandler("Theatre3SelectNodeRequest")]
    public static void Theatre3SelectNodeRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3SelectNodeRequest, Theatre3SelectNodeResponse>(session, packet, (m, req, res) =>
        {
            var step = CurrentStep(m, 2);
            var node = ActiveNode(m, step);
            Require(node.NodeId == req.NodeId, 20203026);
            Require(node.Selected == 0 && !(step.NodeData?.Selected > 0) && !(step.ConnectNodeData?.Selected > 0), 20203075);
            var slot = node.Slots.SingleOrDefault(s => s.SlotId == req.SlotId);
            Require(slot != null, 20203021);
            node.Selected = slot!.Selected = 1;
            int id = slot.SlotType switch { 1 => slot.FightId, 2 => slot.EventId, _ => slot.ShopId };
            int key = EncounterKey(slot.SlotType, id);
            m.State.EncounterUseCounts[key] = m.State.EncounterUseCounts.GetValueOrDefault(key) + 1;
            if (slot.LinkGroupId > 0 && !m.State.ActiveLinkGroups.Contains(slot.LinkGroupId)) m.State.ActiveLinkGroups.Add(slot.LinkGroupId);
            if (slot.SlotType == 2) { m.State.RunEventIds.Add(slot.EventId); EnterEventStep(m, slot, slot.CurStepId); }
        });

    [RequestPacketHandler("Theatre3EndNodeRequest")]
    public static void Theatre3EndNodeRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3EndNodeRequest, Theatre3EndNodeResponse>(session, packet, (m, req, res) =>
        {
            var step = CurrentStep(m, 2);
            var slot = CurrentSlot(m);
            Require(slot.SlotType == 3, 20203022);
            var shop = NodeRows<Theatre3NodeShopTable>().Single(x => x.Id == slot.ShopId);
            if (shop.RewardBoxId is > 0 && m.State.AppliedEffectTriggers.Add($"shop-end-reward:{step.Uid}"))
                AwardNodeReward(m, NewReward(m, shop.RewardBoxType ?? 0, shop.RewardBoxId.Value));
            CompleteNode(m);
        });

    internal static void CompleteNode(Mutation m)
    {
        var step = NodeStep(m);
        m.State.AppliedEffectTriggers.Add($"node-complete:{step.Uid}");
        if (CurrentStep(m).StepType != 2) return; // Pending child retains its originating node.
        var node = ActiveNode(m, step);
        var slot = CurrentSlot(m);
        var db = m.Data.CurChapterDb!;
        step.Overdue = 1;
        db.PassNodeCount++; m.State.RunNodeCount++;
        db.PassNodeIds.Add(node.NodeId); m.State.RunPassedNodeIds.Add(node.NodeId);
        var pair = node == step.NodeData ? step.ConnectNodeData : step.NodeData;
        if (pair != null && !db.PassNodeIds.Contains(pair.NodeId)) db.PassNodeIds.Add(pair.NodeId);
        if (slot.SlotType == 1) db.PassFightCount++;
        if (slot.SlotType == 2) db.PassEventCount++;
        if (slot.SlotType == 3) db.PassShopCount++;
        ApplyEffectTrigger(m, Theatre3EffectTrigger.NodeCompleted, step.Uid);
        ApplyPendingLineSwitchAtNodeBoundary(m);
        if (!db.Steps.Any(s => s.Overdue == 0)) GenerateNextNode(m);
    }

    internal static void OnFightResult(Mutation m, bool won)
    {
        var slot = CurrentSlot(m);
        var node = ActiveNode(m, NodeStep(m));
        if (!won)
        {
            var source = NodeRows<Theatre3NodeTable>().Single(x => x.Id == node.NodeId);
            bool survive = source.UnDefeatNode == 1 || (slot.SlotType == 2 && TableReaderV2.Parse<AscNet.Table.V2.client.theatre3.Theatre3EventNodeTable>().Any(x => x.EventId == slot.EventId && x.UnDefeatNode == 1));
            if (!survive) { SettleRun(m, true); return; }
            if (slot.SlotType == 2) { slot.CurStepId = 0; m.Push(new NotifyTheatre3NodeNextStep { EventId = slot.EventId, NextStepId = 0 }); }
            CompleteNode(m);
            return;
        }
        m.State.RunFightCount++; m.State.TotalFightCount++;
        m.State.RunFightNodeIds.Add(slot.FightId);
        RecordProgress(m, 104019);
        var rewardStep = new Theatre3Step { StepType = 3, Uid = AllocateUid(m), FightRewards = Clone(slot.NodeRewards) };
        foreach (var reward in rewardStep.FightRewards) { reward.Uid = AllocateUid(m); reward.Received = 0; }
        PushStep(m, rewardStep, NodeStep(m).Uid);
        ApplyEffectTrigger(m, NodeRows<Theatre3FightNodeTable>().Single(x => x.Id == slot.FightId).IsBoss == 1
            ? Theatre3EffectTrigger.BossWon : Theatre3EffectTrigger.FightWon, rewardStep.Uid);
        if (slot.SlotType == 2 && !m.Data.PassEventFightNodes.Contains(slot.FightId))
        {
            m.Data.PassEventFightNodes.Add(slot.FightId);
            m.Push(new NotifyTheatre3PassFightNode { PassEventFightNodes = m.Data.PassEventFightNodes.ToList() });
        }
    }

    [RequestPacketHandler("Theatre3RecvFightRewardRequest")]
    public static void Theatre3RecvFightRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3RecvFightRewardRequest, Theatre3RecvFightRewardResponse>(session, packet, (m, req, res) =>
        {
            var step = CurrentStep(m, 3);
            var reward = step.FightRewards.SingleOrDefault(x => x.Uid == req.Uid);
            Require(reward != null, 20203044);
            int firstGrant = m.Grants.Count;
            AwardNodeReward(m, reward!);
            foreach (var grant in m.Grants.Skip(firstGrant))
                foreach (var goods in grant.Goods)
                {
                    var tableGoods = new AscNet.Table.V2.share.reward.RewardGoodsTable
                    { Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count, Params = goods.Params };
                    res.RewardGoodsList.Add(new RewardGoods
                    {
                        Id = goods.Id, TemplateId = goods.TemplateId, Count = goods.Count,
                        RewardType = (int)(RewardHandler.GetRewardType(tableGoods) ?? throw new InvalidDataException("Unknown Theatre3 node reward."))
                    });
                }
        });

    [RequestPacketHandler("Theatre3EndRecvFightRewardRequest")]
    public static void Theatre3EndRecvFightRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3EndRecvFightRewardRequest, Theatre3EndRecvFightRewardResponse>(session, packet, (m, req, res) =>
        {
            var step = CurrentStep(m, 3);
            Require(step.FightRewards.All(x => x.Received != 0), 20203044);
            step.Overdue = 1;
            if (CurrentSlot(m).SlotType == 2)
            {
                AdvanceEventAfterFight(m);
                if (m.Data.DifficultyId != 0) PublishNodeSnapshot(m);
            }
            else CompleteNode(m);
        });
}
