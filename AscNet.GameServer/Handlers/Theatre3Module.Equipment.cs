using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre3;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    // Documented local selection policy: weighted draws without replacement, conditional
    // item pools with SafeGroupId fallback, additive suit-level/character affinity weights.
    // Source declares no item lifetime or box free-refresh values in this dataset: both
    // are zero, not missing bonuses. Live counts elapsed clears; zero expiry is permanent.
    internal static bool CanAddItem(Mutation m, int itemId, int count = 1)
    {
        var row = TableReaderV2.Parse<Theatre3ItemTable>().FirstOrDefault(x => x.Id == itemId);
        // UnlockConditionId gates encyclopedia display, not authored rewards. Pool rows
        // supply their own InitialCondition; applying history gates here deadlocks rewards
        // whose own event completion unlocks the item.
        return row != null && count > 0
            && (row.ObtainMaxCount <= 0 || m.Data.Items.Count(x => x.ItemId == itemId) + count <= row.ObtainMaxCount);
    }

    internal static void AddItem(Mutation m, int itemId, int count = 1)
    {
        var item = TableReaderV2.Parse<Theatre3ItemTable>().FirstOrDefault(x => x.Id == itemId);
        Require(item != null, 20203042);
        Require(CanAddItem(m, itemId, count), 20203056);
        List<Theatre3Item> added = [];
        for (int i = 0; i < count; i++) added.Add(new() { Uid = AllocateUid(m), ItemId = itemId, Live = 0 });
        m.Data.Items.AddRange(added);
        m.State.RunItemsObtainedByQuality[item!.Quality] = checked(m.State.RunItemsObtainedByQuality.GetValueOrDefault(item.Quality) + count);
        m.State.ItemsObtainedByQuality[item.Quality] = checked(m.State.ItemsObtainedByQuality.GetValueOrDefault(item.Quality) + count);
        if (!m.Data.UnlockItemId.Contains(itemId))
        {
            m.Data.UnlockItemId.Add(itemId);
            m.State.RunNewItemIds.Add(itemId);
        }
        m.Push(new NotifyTheatre3AddItem { InnerItems = added });
        RecordProgress(m, 104002, count);
        ApplyEffectTrigger(m, Theatre3EffectTrigger.ItemAdded, itemId, count);
    }

    private static List<int> DrawLoot(List<(int Id, long Weight)> pool, int count)
    {
        List<int> result = [];
        while (result.Count < count && pool.Count > 0)
        {
            long total = pool.Sum(x => Math.Max(0, x.Weight));
            if (total <= 0) break;
            long roll = Random.Shared.NextInt64(total);
            int selected = 0;
            for (; selected < pool.Count - 1; selected++)
            {
                roll -= Math.Max(0, pool[selected].Weight);
                if (roll < 0) break;
            }
            int id = pool[selected].Id;
            result.Add(id);
            pool.RemoveAll(x => x.Id == id);
        }
        return result;
    }

    internal static void OpenItemBox(Mutation m, int boxId, int count = 1, int rootUid = 0)
    {
        var box = TableReaderV2.Parse<Theatre3ItemBoxTable>().FirstOrDefault(x => x.Id == boxId);
        Require(box != null && count > 0, 20203078);
        for (int i = 0; i < count; i++)
        {
            List<int> offered = [];
            foreach (int group in box!.ItemGroupId.Where(x => x > 0))
            {
                var pool = ItemPool(group);
                if (pool.Count == 0) pool = ItemPool(box.SafeGroupId);
                offered.AddRange(DrawLoot(pool, 1));
            }
            if (offered.Count == 0)
            {
                AwardGoldGroup(m, EquipmentConfigInt("FloorGoldGroupId"));
                continue;
            }
            PushStep(m, new Theatre3Step { StepType = 4, ItemIds = offered }, rootUid);

            List<(int Id, long Weight)> ItemPool(int group) => TableReaderV2.Parse<Theatre3ItemGroupTable>()
                .Where(x => x.GroupId == group && x.Weight > 0 && !offered.Contains(x.ItemId)
                    && CanAddItem(m, x.ItemId) && (!(x.InitialCondition > 0) || IsConditionSatisfied(m, x.InitialCondition.Value)))
                .Select(x => (x.ItemId, (long)x.Weight)).ToList();
        }
    }

    private static int EquipmentConfigInt(string key) => checked((int)TableReaderV2.Parse<Theatre3ConfigTable>().Single(x => x.Key == key).Value);

    internal static void OpenEquipBox(Mutation m, int boxId, int count = 1, int rootUid = 0)
    {
        var box = TableReaderV2.Parse<Theatre3EquipBoxTable>().FirstOrDefault(x => x.Id == boxId);
        Require(box != null && count > 0, 20203057);
        for (int i = 0; i < count; i++)
        {
            List<int> offers = EquipOffers(m, box!);
            if (offers.Count == 0)
            {
                AwardGoldGroup(m, EquipmentConfigInt("FloorGoldGroupId"));
                continue;
            }
            var modifiers = GetEffects(m);
            PushStep(m, new Theatre3Step
            {
                StepType = 6, EquipBoxId = boxId, EquipBoxType = box!.Type == 2 ? 2 : 0,
                EquipIds = offers, FreeRefreshLimit = Math.Max(0, modifiers.FreeRefreshCount),
                RefreshDiscount = (double)Math.Clamp(1m - modifiers.RefreshPriceMultiplier, 0m, 1m)
            }, rootUid);
        }
    }

    internal static void OpenInheritedEquips(Mutation m, IEnumerable<int> previousEquipIds)
    {
        var previous = previousEquipIds.ToHashSet();
        var offers = DrawLoot(TableReaderV2.Parse<Theatre3EquipTable>().Where(x => previous.Contains(x.Id))
            .Select(x => (x.Id, 1L)).ToList(), EquipmentConfigInt("InheritEquipSelectCount"));
        // Inheritance retains real piece identity, not prior quantum activation or counters.
        if (offers.Count > 0) PushStep(m, new Theatre3Step { StepType = 7, EquipIds = offers });
    }

    private static void EnsureEquipmentSuitPool(Mutation m)
    {
        if (m.State.EligibleEquipSuitIds.Count > 0) return;
        var rows = TableReaderV2.Parse<Theatre3EquipSuitOnStartTable>();
        var suits = TableReaderV2.Parse<Theatre3EquipSuitTable>().ToDictionary(x => x.Id);
        var selected = m.State.EligibleEquipSuitIds;
        selected.AddRange(rows.Where(x => x.EnsureFlag > 0).Select(x => x.SuitId));
        int count = EquipmentConfigInt("InitSelectedEquipSuitCount");
        int type = EquipmentConfigInt("InitSelectedEquipSuitType");
        int minimum = EquipmentConfigInt("InitSelectedEquipSuitDownLimit");
        int maximum = EquipmentConfigInt("InitSelectedEquipSuitUpLimit");
        // Local starting-pool rule: guaranteed suits first, then weighted draws. Reserve
        // enough remaining places for the configured UseType lower bound and enforce its
        // upper bound; exclusions are symmetric, selected-row affinity bonuses additive.
        while (selected.Count < count)
        {
            int typed = selected.Count(id => suits[id].UseType == type);
            var pool = new List<(int Id, long Weight)>();
            foreach (var row in rows.Where(x => !selected.Contains(x.SuitId)))
            {
                bool isType = suits[row.SuitId].UseType == type;
                if ((isType && typed >= maximum) || (!isType && count - selected.Count <= minimum - typed)) continue;
                if (row.ExcludeSuitIds.Any(selected.Contains)
                    || rows.Any(x => selected.Contains(x.SuitId) && x.ExcludeSuitIds.Contains(row.SuitId))) continue;
                long weight = m.State.PreviousEquipSuitIds.Contains(row.SuitId) ? row.RepeatWeight : row.Weight;
                foreach (var chosen in rows.Where(x => selected.Contains(x.SuitId)))
                    for (int i = 0; i < Math.Min(chosen.AddWeightSuitIds.Count, chosen.AddWeights.Count); i++)
                        if (chosen.AddWeightSuitIds[i] == row.SuitId) weight += chosen.AddWeights[i];
                if (weight > 0) pool.Add((row.SuitId, weight));
            }
            var draw = DrawLoot(pool, 1);
            Require(draw.Count > 0, 20203057);
            selected.Add(draw[0]);
        }
    }

    private static List<int> EquipOffers(Mutation m, Theatre3EquipBoxTable box, IReadOnlyCollection<int>? excluded = null, int? count = null)
    {
        EnsureEquipmentSuitPool(m);
        var equipment = TableReaderV2.Parse<Theatre3EquipTable>().ToDictionary(x => x.Id);
        List<(int Id, long Weight)> pool = [];
        foreach (var row in TableReaderV2.Parse<Theatre3EquipGroupTable>().Where(x => x.GroupId == box.EquipGroupId))
        {
            if (!equipment.TryGetValue(row.EquipId, out var equip)) continue;
            if (excluded?.Contains(box.Type == 2 ? equip.SuitId : equip.Id) == true) continue;
            if (!m.State.EligibleEquipSuitIds.Contains(equip.SuitId) && !m.Data.Equips.Any(x => x.SuitId == equip.SuitId)) continue;
            if (box.Type == 2 ? !CanQuantumSuit(m, equip.SuitId) : m.Data.Equips.Any(x => x.EquipId == equip.Id)) continue;
            int owned = m.Data.Equips.Count(x => x.SuitId == equip.SuitId);
            long weight = row.Weight ?? 0;
            for (int i = 0; i < Math.Min(row.SuitLevel.Count, row.SuitLevelAddWeight.Count); i++)
                if (owned == row.SuitLevel[i]) weight += row.SuitLevelAddWeight[i];
            if (box.CharacterAddWeight > 0 && TableReaderV2.Parse<Theatre3CharacterRecruitTable>().Any(r => r.Property == equip.AttType
                && m.Data.EquipPos.Any(p => (p.CardId > 0 && p.CardId == r.CharacterId) || (p.RobotId > 0 && p.RobotId == r.RobotId))))
                weight += box.CharacterAddWeight.Value;
            if (weight > 0) pool.Add((box.Type == 2 ? equip.SuitId : equip.Id, weight));
        }
        // Quantum offers carry real suit IDs, combining their constituent piece weights.
        return DrawLoot(pool.GroupBy(x => x.Id).Select(x => (x.Key, x.Sum(y => y.Weight))).ToList(), count ?? box.ExtractCount);
    }
    internal static bool ReconcileEquipmentStep(Mutation m, Theatre3Step step)
    {
        if (step.Overdue != 0) return false;
        if (step.StepType == 4)
        {
            if (step.ItemIds.RemoveAll(id => !CanAddItem(m, id)) == 0) return false;
            if (step.ItemIds.Count > 0) return true;
        }
        else if (step.StepType is 6 or 7)
        {
            int removed = step.EquipIds.RemoveAll(id => step.EquipBoxType == 2
                ? !CanQuantumSuit(m, id) : m.Data.Equips.Any(x => x.EquipId == id));
            if (removed == 0) return false;
            if (step.StepType == 6)
            {
                var box = TableReaderV2.Parse<Theatre3EquipBoxTable>().Single(x => x.Id == step.EquipBoxId);
                step.EquipIds.AddRange(EquipOffers(m, box, step.EquipIds, removed));
            }
            if (step.EquipIds.Count > 0) return true;
        }
        else return false;
        // Only a newly foreground exhausted reward resolves automatically; no paid
        // refresh is consumed, and the scheduler publishes replacement state once.
        AwardGoldGroup(m, EquipmentConfigInt("FloorGoldGroupId"));
        step.Overdue = 1;
        return true;
    }


    private static bool CanQuantumSuit(Mutation m, int suitId)
    {
        var owned = m.Data.Equips.Where(x => x.SuitId == suitId).ToList();
        var suit = TableReaderV2.Parse<Theatre3EquipSuitTable>().FirstOrDefault(x => x.Id == suitId);
        return owned.Count > 0 && owned.All(x => !x.QubitActive) && suit != null && suit.QubitSuitEffectGroupId > 0;
    }

    private static void ValidateEquipPlacement(Mutation m, int suitId, int pos)
    {
        var slot = m.Data.EquipPos.FirstOrDefault(x => x.PosId == pos);
        Require(slot != null, 20203050);
        var owned = m.Data.Equips.Where(x => x.SuitId == suitId).ToList();
        // Slots bind equipment to the persisted character/color; neither is rewritten by loot.
        Require(owned.Count == 0 || owned.All(x => x.Pos == pos), 20203047);
        Require(owned.Count > 0 || m.Data.Equips.Where(x => x.Pos == pos).Select(x => x.SuitId).Distinct().Count() < slot!.Capacity, 20203047);
    }

    internal static void OpenWorkshop(Mutation m, int workshopId, int rootUid = 0)
    {
        var workshop = TableReaderV2.Parse<Theatre3WorkShopTable>().FirstOrDefault(x => x.Id == workshopId);
        Require(workshop != null, 20203039);
        var extra = GetEffects(m).WorkshopExtraUses.GetValueOrDefault(workshop!.Type);
        PushStep(m, new Theatre3Step { StepType = 5, WorkShopId = workshopId, WorkShopType = workshop.Type,
            WorkShopTotalCount = Math.Max(0, workshop.Count + extra) }, rootUid);
    }

    private static Theatre3Step EquipmentWorkshop(Mutation m, int type)
    {
        var step = CurrentStep(m, 5);
        Require(step.WorkShopType == type, 20203019);
        Require(step.WorkShopCurCount < step.WorkShopTotalCount, 20203048);
        return step;
    }

    [RequestPacketHandler("Theatre3SelectItemRewardRequest")]
    public static void SelectItemReward(Session session, Packet.Request packet) =>
        Handle<Theatre3SelectItemRewardRequest, Theatre3SelectItemRewardResponse>(session, packet, (m, req, res) =>
        {
            var step = CurrentStep(m, 4);
            Require(step.SelectedItemId == 0, 20203041);
            Require(step.ItemIds.Contains(req.InnerItemId), 20203042);
            AddItem(m, req.InnerItemId);
            step.SelectedItemId = req.InnerItemId;
            res.InnerItemId = req.InnerItemId;
            FinishStep(m, step);
        });

    [RequestPacketHandler("Theatre3SelectEquipRequest")]
    public static void SelectEquip(Session session, Packet.Request packet) =>
        Handle<Theatre3SelectEquipRequest, Theatre3SelectEquipResponse>(session, packet, (m, req, res) =>
        {
            var step = CurrentStep(m, 6, 7);
            Require(step.SelectedEquipId == 0, 20203058);
            Require(step.EquipIds.Contains(req.SelectId), 20203050);
            if (step.EquipBoxType == 2)
            {
                Require(CanQuantumSuit(m, req.SelectId), 20203070);
                ActivateQuantumSuit(m, req.SelectId);
            }
            else
            {
                var equip = TableReaderV2.Parse<Theatre3EquipTable>().FirstOrDefault(x => x.Id == req.SelectId);
                Require(equip != null, 20203057);
                Require(m.Data.Equips.All(x => x.EquipId != req.SelectId), 20203046);
                ValidateEquipPlacement(m, equip!.SuitId, req.Pos);
                var added = new Theatre3Equip { EquipId = equip.Id, SuitId = equip.SuitId, Pos = req.Pos,
                    QubitActive = m.Data.Equips.Any(x => x.SuitId == equip.SuitId && x.QubitActive) };
                m.Data.Equips.Add(added);
                if (!m.Data.UnlockEquipId.Contains(equip.Id))
                {
                    m.Data.UnlockEquipId.Add(equip.Id);
                    m.State.RunNewEquipIds.Add(equip.Id);
                }
                m.Push(new NotifyTheatre3EquipDatas { Equips = [added] });
                ApplyEffectTrigger(m, Theatre3EffectTrigger.EquipmentChanged, equip.SuitId);
            }
            step.SelectedEquipId = req.SelectId;
            FinishStep(m, step);
        });

    [RequestPacketHandler("Theatre3RefreshEquipBoxRequest")]
    public static void RefreshEquipBox(Session session, Packet.Request packet) =>
        Handle<Theatre3RefreshEquipBoxRequest, Theatre3RefreshEquipBoxResponse>(session, packet, (m, req, res) =>
        {
            var step = CurrentStep(m, 6);
            var box = TableReaderV2.Parse<Theatre3EquipBoxTable>().Single(x => x.Id == step.EquipBoxId);
            Require(step.RefreshTimes < step.FreeRefreshLimit + box.RefreshCostNum, 20203077);
            int paidIndex = step.RefreshTimes - step.FreeRefreshLimit;
            int cost = paidIndex < 0 ? 0 : checked((int)decimal.Floor(box.RefreshCost[Math.Min(paidIndex, box.RefreshCost.Count - 1)] * (1m - (decimal)step.RefreshDiscount)));
            var offers = EquipOffers(m, box);
            Require(offers.Count > 0, 20203050);
            if (cost > 0) AddCoin(m, -cost);
            step.EquipIds = offers;
            step.RefreshTimes++;
            ApplyEffectTrigger(m, Theatre3EffectTrigger.BoxRefresh, step.Uid, step.RefreshTimes);
            res.Step = Clone(step);
        });

    [RequestPacketHandler("Theatre3ChangeEquipPosRequest")]
    public static void ChangeEquipPos(Session session, Packet.Request packet) =>
        Handle<Theatre3ChangeEquipPosRequest, Theatre3ChangeEquipPosResponse>(session, packet, (m, req, res) =>
        {
            var step = EquipmentWorkshop(m, 2);
            var src = m.Data.Equips.Where(x => x.SuitId == req.SrcSuitId && x.Pos == req.SrcPos).ToList();
            var dst = m.Data.Equips.Where(x => x.SuitId == req.DstSuitId && x.Pos == req.DstPos).ToList();
            Require(src.Count > 0, 20203049);
            Require(req.SrcPos != req.DstPos && req.SrcSuitId != req.DstSuitId, 20203051);
            Require(req.DstSuitId == 0 || dst.Count > 0, 20203051);
            Require(m.Data.EquipPos.Any(x => x.PosId == req.DstPos), 20203051);
            if (dst.Count == 0) ValidateEquipDestination(m, req.DstPos);
            foreach (var equip in src) equip.Pos = req.DstPos;
            foreach (var equip in dst) equip.Pos = req.SrcPos;
            step.WorkShopCurCount++;
            m.Push(new NotifyTheatre3EquipDatas { Equips = src.Concat(dst).ToList() });
            ApplyEffectTrigger(m, Theatre3EffectTrigger.EquipmentChanged, req.SrcSuitId);
        });

    private static void ValidateEquipDestination(Mutation m, int pos)
    {
        var slot = m.Data.EquipPos.FirstOrDefault(x => x.PosId == pos);
        Require(slot != null && m.Data.Equips.Where(x => x.Pos == pos).Select(x => x.SuitId).Distinct().Count() < slot.Capacity, 20203047);
    }

    [RequestPacketHandler("Theatre3RecastEquipRequest")]
    public static void RecastEquip(Session session, Packet.Request packet) =>
        Handle<Theatre3RecastEquipRequest, Theatre3RecastEquipResponse>(session, packet, (m, req, res) =>
        {
            var step = EquipmentWorkshop(m, 1);
            var src = m.Data.Equips.FirstOrDefault(x => x.EquipId == req.SrcEquipId);
            Require(src != null, 20203052);
            Require(!src!.QubitActive, 20203071);
            var destination = m.Data.Equips.FirstOrDefault(x => x.SuitId == req.DstSuitId);
            Require(destination != null && destination.SuitId != src.SuitId, 20203051);
            Require(!destination!.QubitActive, 20203071);
            // Local rebuild policy: uniformly choose an unowned real piece of the requested
            // existing suit; its persisted container determines position, not a client field.
            var candidates = TableReaderV2.Parse<Theatre3EquipTable>().Where(x => x.SuitId == req.DstSuitId
                && m.Data.Equips.All(y => y.EquipId != x.Id)).ToList();
            Require(candidates.Count > 0, 20203053);
            var chosen = candidates[Random.Shared.Next(candidates.Count)];
            m.Data.Equips.Remove(src);
            var added = new Theatre3Equip { EquipId = chosen.Id, SuitId = chosen.SuitId, Pos = destination.Pos };
            m.Data.Equips.Add(added);
            if (!m.Data.UnlockEquipId.Contains(chosen.Id))
            {
                m.Data.UnlockEquipId.Add(chosen.Id);
                m.State.RunNewEquipIds.Add(chosen.Id);
            }
            step.WorkShopCurCount++;
            res.DstEquipId = chosen.Id;
            // Recast callback removes source and adds destination; an additive equip push
            // cannot remove the old record and must not pretend to replace the whole list.
            ApplyEffectTrigger(m, Theatre3EffectTrigger.EquipmentChanged, chosen.SuitId);
        });

    [RequestPacketHandler("Theatre3QubitEquipRequest")]
    public static void QubitEquip(Session session, Packet.Request packet) =>
        Handle<Theatre3QubitEquipRequest, Theatre3QubitEquipResponse>(session, packet, (m, req, res) =>
        {
            var step = EquipmentWorkshop(m, 3);
            Require(CanQuantumSuit(m, req.EquipId), 20203070);
            ActivateQuantumSuit(m, req.EquipId);
            step.WorkShopCurCount++;
        });

    [RequestPacketHandler("Theatre3LookEquipAttributeRequest")]
    public static void LookEquipAttribute(Session session, Packet.Request packet)
    {
        Theatre3LookEquipAttributeResponse response = new();
        try
        {
            var request = packet.Content is not { Length: > 0 } || (packet.Content.Length == 1 && packet.Content[0] == 0xc0)
                ? new Theatre3LookEquipAttributeRequest() : packet.Deserialize<Theatre3LookEquipAttributeRequest>();
            var state = session.player.Theatre3;
            var equip = state.Data.Equips.FirstOrDefault(x => x.EquipId == request.EquipId);
            if (request.EquipId < 0 || (request.EquipId > 0 && equip == null)) response.Code = 20203050;
            else if (request.SuitId < 0 || (request.SuitId > 0 && !state.Data.Equips.Any(x => x.SuitId == request.SuitId)))
                response.Code = 20203049;
            else
            {
                response.CurBuyCount = state.EffectCounters.GetValueOrDefault("CurBuyCount");
                response.TotalRecvCoin = state.EffectCounters.GetValueOrDefault("TotalRecvCoin");
                if (request.EquipId > 0)
                    response.Equip = new Theatre3EquipAttribute { EquipId = request.EquipId,
                        PassFightCount = equip!.PassFightCount, PassBossFightCount = equip.PassBossFightCount };
                if (request.SuitId > 0)
                    response.EquipSuit = new Theatre3SuitAttribute { SuitId = request.SuitId,
                        PassFightCount = state.EffectCounters.GetValueOrDefault($"suit:{request.SuitId}:fight"),
                        PassBossFightCount = state.EffectCounters.GetValueOrDefault($"suit:{request.SuitId}:boss") };
            }
        }
        catch (MessagePack.MessagePackSerializationException) { response.Code = 1; }
        // Deliberately bypass mutation receipts: this endpoint reads current counters and
        // has no claim, cooldown, clock, random draw, reward, push or durable state change.
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("Theatre3EndEquipBoxRequest")]
    public static void EndEquipBox(Session session, Packet.Request packet) =>
        Handle<Theatre3EndEquipBoxRequest, Theatre3EndEquipBoxResponse>(session, packet, (m, req, res) => { CurrentStep(m, 6, 7); CompleteStep(m); });

    [RequestPacketHandler("Theatre3EndWorkShopRequest")]
    public static void EndWorkShop(Session session, Packet.Request packet) =>
        Handle<Theatre3EndWorkShopRequest, Theatre3EndWorkShopResponse>(session, packet, (m, req, res) => { CurrentStep(m, 5); CompleteStep(m); });
}
