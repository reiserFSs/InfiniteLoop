using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.theatre;

namespace AscNet.GameServer.Handlers;

internal static partial class TheatreModule
{
    internal static void InitialiseEventSlot(Mutation mutation, TheatreSlot slot, int eventId)
    {
        // LOCAL routing policy: authored row order chooses the entry, not arithmetic on StepId.
        // Persist this choice on the slot; subsequent requests never roll an event again.
        var first = Rows<TheatreEventTable>().Where(row => row.EventId == eventId)
            .OrderBy(row => row.Id).FirstOrDefault();
        Require(first != null, 20155029);
        slot.ConfigId = eventId;
        mutation.State.EventVisitedSteps[slot.SlotId] = [];
        SetEventStep(slot, first!);
    }

    [RequestPacketHandler("TheatreEventNodeNextStepRequest")]
    public static void EventNodeNextStep(Session session, Packet.Request packet) =>
        Handle<TheatreEventNodeNextStepRequest, TheatreEventNodeNextStepResponse>(session, packet,
            (mutation, request, response) =>
            {
                TheatreSlot slot = CurrentSlot(mutation);
                Require(slot.SlotType == 1, 20155025);
                Require(slot.CurStepId > 0 && slot.CurStepId == request.CurStepId, 20155028);
                var row = EventStep(slot.ConfigId, slot.CurStepId);
                // Battle steps advance only through the validated fight result, never this RPC.
                Require(row.Type is 1 or 2 or 4 or 6, 20155029);
                int next = row.NextStepId ?? 0;
                if (row.Type == 2)
                {
                    // Lua sends the original one-based authored index, NOT the visible option position.
                    int index = (request.OptionId ?? 0) - 1;
                    Require(index >= 0 && index < row.OptionDesc.Count
                        && !string.IsNullOrWhiteSpace(row.OptionDesc[index]), 20155030);
                    int kind = row.OptionType.ElementAtOrDefault(index);
                    Require(kind is >= 1 and <= 4, 20155029);
                    next = row.OptionNextStepId.ElementAtOrDefault(index);
                    if (kind is 1 or 2)
                    {
                        int itemId = row.OptionItemId.ElementAtOrDefault(index);
                        int count = row.OptionItemCount.ElementAtOrDefault(index);
                        Require(itemId > 0 && count > 0, 20155029);
                        Require(mutation.Balance(itemId) >= count, 20012004);
                        // Authored ConsumeItem spends; CheckHasItem deliberately leaves it intact.
                        if (kind == 1) mutation.Cost(itemId, count);
                    }
                }
                else Require(request.OptionId is null or 0, 20155030);

                if (row.Type == 4)
                {
                    int itemId = row.StepRewardItemId ?? 0;
                    int count = row.StepRewardItemCount ?? 0;
                    Require(itemId > 0 && count > 0, 20155029);
                    // Economic policy: exactly the authored permanent item/count, no reward multiplier.
                    var goods = new RewardGoodsTable { Id = itemId, TemplateId = itemId, Count = count, Params = [] };
                    var rewardType = RewardHandler.GetRewardType(goods);
                    Require(rewardType != null, 20155029);
                    mutation.Grant(new RewardGrant(
                        $"theatre:event:{mutation.Session.player.PlayerData.Id}:{mutation.State.RunId}:{slot.SlotId}:{row.StepId}", [goods]));
                    response.RewardGoodsList.Add(new RewardGoods
                    {
                        Id = itemId, TemplateId = itemId, Count = count, RewardType = (int)rewardType!.Value
                    });
                }
                MoveEventStep(mutation, slot, row, next, afterFight: false);
                response.NextStepId = next;
            });

    internal static void AdvanceEventAfterFight(Mutation mutation, TheatreSlot slot)
    {
        Require(slot.SlotType == 1, 20155025);
        Require(slot.CurStepId > 0, 20155028);
        var row = EventStep(slot.ConfigId, slot.CurStepId);
        Require(row.Type == 5 && row.StageId > 0 && slot.PassedStageIds.Contains(row.StageId.Value), 20155029);
        MoveEventStep(mutation, slot, row, row.NextStepId ?? 0, afterFight: true);
    }

    private static TheatreEventTable EventStep(int eventId, int stepId)
    {
        var rows = Rows<TheatreEventTable>().Where(row => row.EventId == eventId && row.StepId == stepId).ToList();
        Require(rows.Count == 1, 20155029);
        return rows[0];
    }

    private static void SetEventStep(TheatreSlot slot, TheatreEventTable row)
    {
        Require(row.StepId > 0 && row.Type is 1 or 2 or 4 or 5 or 6, 20155029);
        // EN authors no type-3 local reward rows, keepsake rewards, NeedDecoration, or
        // OptionNeedDecoration values. Those columns are omitted by the table generator.
        // Fail closed for unauthored variants rather than guess item scope or condition semantics.
        if (row.Type == 2)
            Require(row.OptionDesc.Where((description, index) => !string.IsNullOrWhiteSpace(description)
                && row.OptionType.ElementAtOrDefault(index) is >= 1 and <= 4).Any(), 20155029);
        if (row.Type == 5) Require(row.StageId > 0, 20155029);
        if (row.Type == 6) Require(!string.IsNullOrWhiteSpace(row.StoryId), 20155029);
        slot.CurStepId = row.StepId;
        slot.StageIds = row.Type == 5 ? [row.StageId!.Value] : [];
        slot.PassedStageIds.Clear();
        slot.PassedStageIndexs.Clear();
        slot.TheatreStageId = 0; // Event StageId is a native stage, not a TheatreStage row ID.
        slot.StoryId = row.Type == 6 ? row.StoryId! : string.Empty;
    }

    private static void MoveEventStep(Mutation mutation, TheatreSlot slot, TheatreEventTable row, int next, bool afterFight)
    {
        Require(mutation.State.EventVisitedSteps.TryGetValue(slot.SlotId, out var visited)
            && !visited.Contains(row.StepId), 20155028);
        Require(next >= 0 && (next == 0 || next != row.StepId && !visited!.Contains(next)), 20155029);
        var nextRow = next > 0 ? EventStep(slot.ConfigId, next) : null;
        visited!.Add(row.StepId);
        if (!mutation.Data.PassEventRecord.TryGetValue(row.EventId, out var steps))
            mutation.Data.PassEventRecord[row.EventId] = steps = [];
        // LOCAL wire encoding: Lua checks truthy presence, not the leaf's scalar type.
        steps[row.StepId] = true;
        if (nextRow != null) SetEventStep(slot, nextRow);
        else slot.CurStepId = 0;
        // The Lua fight consumer requires this push before the next node can replace the chapter.
        // Ordinary RPC callers update from NextStepId in their response instead.
        if (afterFight) mutation.Push(new TheatreNodeNextStep { EventId = row.EventId, NextStepId = next });
        if (next == 0) CompleteNode(mutation);
    }
}
