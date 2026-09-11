using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.theatre3;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    [RequestPacketHandler("Theatre3EventNodeNextStepRequest")]
    public static void EventNodeNextStep(Session session, Packet.Request packet) =>
        Handle<Theatre3EventNodeNextStepRequest, Theatre3EventNodeNextStepResponse>(session, packet, (m, request, response) =>
        {
            Theatre3Step root = CurrentStep(m, 2);
            Theatre3NodeSlot slot = CurrentSlot(m);
            Require(slot.SlotType == 2, 20203022);
            Require(slot.CurStepId > 0 && slot.CurStepId == request.CurEventStepId, 20203038);
            Theatre3EventTable row = EventRow(slot, slot.CurStepId);
            Require(row.Type != 4, 20203030);
            int next = row.NextStepId ?? 0;
            if (row.Type == 2)
            {
                var options = TableReaderV2.Parse<Theatre3EventOptionGroupTable>()
                    .Where(option => option.GroupId == row.OptionGroupId).OrderBy(option => option.Id).ToList();
                Require(request.OptionId > 0 && request.OptionId <= options.Count, 20203040);
                var option = options[request.OptionId - 1];
                Require(IsConditionSatisfied(m, option.OptionShowCondition ?? 0), 20203040);
                var requirements = new Dictionary<int, int>();
                if (option.OptionType is 1 or 2)
                {
                    Require(option.OptionItemType is 1 or 2, 20203039);
                    for (int i = 0; i < option.OptionItemId.Count; i++)
                    {
                        int id = option.OptionItemId[i];
                        int count = option.OptionItemCount.ElementAtOrDefault(i);
                        if (id == 0 && count == 0) continue;
                        Require(id > 0 && count > 0, 20203039);
                        requirements[id] = checked(requirements.GetValueOrDefault(id) + count);
                    }
                    foreach (var requirement in requirements)
                        Require(option.OptionItemType == 1 ? m.Balance(requirement.Key) >= requirement.Value
                            : m.Data.Items.Count(item => item.ItemId == requirement.Key) >= requirement.Value,
                            option.OptionItemType == 1 ? 20203037 : 20203042);
                }
                else Require(option.OptionType == 3, 20203039);

                int groupId = option.OptionNextStepGroupId;
                if (groupId > 0)
                {
                    var candidates = TableReaderV2.Parse<Theatre3EventStepGroupTable>()
                        .Where(candidate => candidate.GroupId == groupId && candidate.Weight > 0
                            && (candidate.NodeEnterLimit <= 0 || slot.PassedStepId.Count(id => id == candidate.StepId)
                                + (slot.CurStepId == candidate.StepId ? 1 : 0) < candidate.NodeEnterLimit)).ToList();
                    Require(candidates.Count > 0, 20203039);
                    next = Weighted(candidates, candidate => candidate.Weight).StepId;
                }
                else next = 0;
                if (next > 0) EventRow(slot, next);
                if (option.OptionType == 1)
                {
                    foreach (var requirement in requirements)
                    {
                        if (option.OptionItemType == 1) m.Cost(requirement.Key, requirement.Value);
                        else
                        {
                            foreach (var item in m.Data.Items.Where(item => item.ItemId == requirement.Key)
                                .Take(requirement.Value).ToList()) m.Data.Items.Remove(item);
                        }
                    }
                    if (option.OptionItemType == 2)
                        m.Push(new NotifyTheatre3Item { InnerItems = m.Data.Items.ToList() });
                }
            }
            else Require(row.Type is 1 or 3 or 5 && request.OptionId is 0 or 1, 20203040);

            if (row.Type == 3)
            {
                for (int i = 0; i < row.StepRewardItemId.Count; i++)
                {
                    int id = row.StepRewardItemId[i];
                    int count = row.StepRewardItemCount.ElementAtOrDefault(i);
                    if (id == 0 && count == 0) continue;
                    Require(id > 0 && count > 0, 20203039);
                    switch (row.StepRewardItemType)
                    {
                        case 1:
                            var goods = new RewardGoodsTable { Id = id, TemplateId = id, Count = count, Params = [] };
                            if (id == 96189) AddCoin(m, count);
                            else m.Grant(new RewardGrant($"theatre3:event:{m.Session.player.PlayerData.Id}:{m.State.RunId}:{root.Uid}:{row.StepId}:{slot.PassedStepId.Count}:{i}", [goods]));
                            response.RewardGoodsList.Add(new RewardGoods
                            {
                                Id = id, TemplateId = id, Count = count,
                                RewardType = (int)(RewardHandler.GetRewardType(goods) ?? throw new InvalidDataException("Unknown event reward item."))
                            });
                            break;
                        case 2:
                            AddItem(m, id, count);
                            response.InnerItemIds.Add(id);
                            break;
                        case 3: OpenItemBox(m, id, count, root.Uid); break;
                        case 4: OpenEquipBox(m, id, count, root.Uid); break;
                        default: Require(false, 20203039); break;
                    }
                }
            }
            MoveEvent(m, slot, row, next);
            response.NextEventStepId = next;
            response.FightTemplateId = slot.FightTemplateId;
        });

    private static Theatre3EventTable EventRow(Theatre3NodeSlot slot, int stepId)
    {
        var row = TableReaderV2.Parse<Theatre3EventTable>().FirstOrDefault(value => value.EventId == slot.EventId && value.StepId == stepId);
        Require(row != null, 20203039);
        return row!;
    }

    internal static void EnterEventStep(Mutation m, Theatre3NodeSlot slot, int stepId)
    {
        var row = EventRow(slot, stepId);
        var root = m.Data.CurChapterDb!.Steps.Last(step => step.StepType == 2 && step.Overdue == 0);
        slot.CurStepId = stepId;
        if (!m.State.AppliedEffectTriggers.Add($"event-enter:{root.Uid}:{stepId}:{slot.PassedStepId.Count}")) return;
        m.State.EventStepEnterCounts[stepId] = checked(m.State.EventStepEnterCounts.GetValueOrDefault(stepId) + 1);
        if (row.Type == 4)
        {
            if (slot.FightId != row.FightNodeId || slot.FightTemplateId <= 0)
                InitializeFightSlot(m, slot, row.FightNodeId ?? 0);
        }
        else
        {
            slot.FightId = 0;
            slot.FightTemplateId = 0;
            if (row.Type == 5) OpenWorkshop(m, row.WorkShopId ?? 0, root.Uid);
            else Require(row.Type is 1 or 2 or 3, 20203039);
        }
        if (row.EffectGroupId > 0) ApplyEffectGroup(m, row.EffectGroupId.Value, AllocateUid(m));
    }

    internal static void AdvanceEventAfterFight(Mutation m)
    {
        var slot = CurrentSlot(m);
        Require(slot.SlotType == 2 && slot.CurStepId > 0, 20203038);
        var row = EventRow(slot, slot.CurStepId);
        Require(row.Type == 4, 20203030);
        int next = row.NextStepId ?? 0;
        MoveEvent(m, slot, row, next);
        m.Push(new NotifyTheatre3NodeNextStep { EventId = slot.EventId, NextStepId = next });
    }

    private static void MoveEvent(Mutation m, Theatre3NodeSlot slot, Theatre3EventTable row, int next)
    {
        slot.PassedStepId.Add(row.StepId);
        if (!m.State.RunPassedEventStepIds.Contains(row.StepId)) m.State.RunPassedEventStepIds.Add(row.StepId);
        if (!m.State.PassedEventStepIds.Contains(row.StepId)) m.State.PassedEventStepIds.Add(row.StepId);
        RecordProgress(m, 104020, 1, row.StepId);
        slot.CurStepId = next;
        if (next > 0) EnterEventStep(m, slot, next);
        else
        {
            slot.FightId = 0;
            slot.FightTemplateId = 0;
            CompleteNode(m);
        }
    }
}
