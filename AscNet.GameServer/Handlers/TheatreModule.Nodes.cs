using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.theatre;
using System.Globalization;

namespace AscNet.GameServer.Handlers;

internal static partial class TheatreModule
{
    [RequestPacketHandler("TheatreSelectNodeRequest")]
    public static void TheatreSelectNodeRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreSelectNodeRequest, TheatreSelectNodeResponse>(session, packet, (mutation, request, _) =>
        {
            Require(mutation.Data.CurChapterDb != null, 20155005);
            Require(!mutation.State.NodeCompletionPending && !HasPendingSkillChoice(mutation), 20155023);
            RequireCanEnterChapter(mutation);
            TheatreNodeData node = mutation.Data.CurChapterDb!.CurNodeDb;
            Require(node.NodeId == request.NodeId, 20155018);
            Require(node.Slots.All(slot => slot.Selected == 0), 20155019);
            TheatreSlot? slot = node.Slots.SingleOrDefault(slot => slot.SlotId == request.SlotId);
            Require(slot != null, 20155020);
            slot!.Selected = 1;
        });

    [RequestPacketHandler("TheatreEndNodeRequest")]
    public static void TheatreEndNodeRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreEndNodeRequest, TheatreEndNodeResponse>(session, packet, (mutation, _, _) =>
        {
            TheatreSlot slot = CurrentSlot(mutation);
            Require(!HasPendingSkillChoice(mutation), 20155023);
            // Movie playback and leaving the node shop are the only client EndNode paths.
            Require(slot.SlotType is 2 or 6, 20155025);
            CompleteNode(mutation);
        });

    internal static TheatreSlot CurrentSlot(Mutation mutation)
    {
        Require(mutation.Data.CurChapterDb != null, 20155005);
        Require(!mutation.State.NodeCompletionPending, 20155017);
        List<TheatreSlot> selected = mutation.Data.CurChapterDb!.CurNodeDb.Slots.Where(slot => slot.Selected == 1).ToList();
        Require(selected.Count == 1, 20155020);
        return selected[0];
    }

    internal static void BeginAdventure(Mutation mutation)
    {
        Require(mutation.Data.CurChapterDb != null, 20155022);
        mutation.State.NodeCursor = 0;
        mutation.State.NodeCompletionPending = false;
        InitializeChapterRecruit(mutation);
        GenerateNextNode(mutation);
    }

    internal static void OnFightResult(Mutation mutation, bool won, int stageIndex)
    {
        TheatreSlot slot = CurrentSlot(mutation);
        Require(slot.SlotType is 1 or 3 or 4 or 5, 20155025);
        Require(stageIndex > 0 && stageIndex <= slot.StageIds.Count, 20155021);
        Require(!slot.PassedStageIndexs.Contains(stageIndex), 20155021);
        if (!won)
        {
            // LOCAL: Combat increments the client-authored USED reopen counter on defeat.
            var difficulty = Rows<TheatreDifficultyTable>().Single(row => row.Id == mutation.Data.DifficultyId);
            if (mutation.Data.ReopenCount >= difficulty.ReopenCount + GetModifiers(mutation).ReopenBonus)
                SettleRun(mutation, abandoned: true);
            return;
        }
        slot.PassedStageIndexs.Add(stageIndex);
        slot.PassedStageIds.Add(slot.StageIds[stageIndex - 1]);
        mutation.State.RunFightCount = checked(mutation.State.RunFightCount + 1);
        if (slot.PassedStageIndexs.Count < slot.StageIds.Count) return;
        if (slot.SlotType == 1) AdvanceEventAfterFight(mutation, slot);
        else CompleteNode(mutation);
    }

    internal static void CompleteNode(Mutation mutation)
    {
        TheatreSlot slot = CurrentSlot(mutation);
        Require(!HasPendingSkillChoice(mutation), 20155023);
        if (slot.SlotType is 3 or 4 or 5)
            Require(slot.StageIds.Count > 0 && slot.PassedStageIndexs.Count == slot.StageIds.Count, 20155021);
        mutation.State.NodeCompletionPending = true;
        mutation.State.NodeCursor = checked(mutation.State.NodeCursor + 1);
        mutation.State.RunNodeCount = checked(mutation.State.RunNodeCount + 1);
        if (slot.SlotType == 1)
        {
            mutation.State.RunEventCount = checked(mutation.State.RunEventCount + 1);
            mutation.State.RunEventIds.Add(slot.ConfigId);
        }
        if (slot.SlotType is 3 or 4 or 5)
        {
            var stage = Rows<TheatreStageTable>().Single(row => row.Id == slot.TheatreStageId);
            if (stage.IsBoss == 1) mutation.State.RunBossCount = checked(mutation.State.RunBossCount + 1);
            if (stage.RewardId.GetValueOrDefault() > 0) mutation.Grant(stage.RewardId!.Value);
        }
        // The client does not count pure movies when AddNode replaces the old node.
        if (slot.SlotType != 6)
        {
            mutation.Data.PassNodeCount = checked(mutation.Data.PassNodeCount + 1);
            mutation.Data.CurChapterDb!.PassNodeCount = checked(mutation.Data.CurChapterDb.PassNodeCount + 1);
        }
        RecordProgress(mutation, 73017);
        RecordKeepsakeNode(mutation);
        int nodeCoin = GetModifiers(mutation).NodeCoinBonus;
        if (nodeCoin > 0)
            mutation.Grant(new RewardGrant(
                $"theatre:node:{mutation.Session.player.PlayerData.Id}:{mutation.State.RunId}:{slot.SlotId}",
                [new RewardGoodsTable { Id = 96101, TemplateId = 96101, Count = nodeCoin }]));
        if (slot.RewardType > 0)
        {
            var chapter = Rows<TheatreChapterTable>().Single(row => row.Id == mutation.Data.CurChapterDb!.ChapterId);
            // LOCAL economics: one skill/level award, authored chapter payout for currency reward buckets.
            int count = slot.RewardType == 3 ? chapter.DecorationPoint : slot.RewardType == 4 ? chapter.FavorPoint : 1;
            ApplyNodeReward(mutation, slot.RewardType, count, slot.PowerId);
        }
        if (!HasPendingSkillChoice(mutation)) GenerateNextNode(mutation);
    }

    internal static void GenerateNextNode(Mutation mutation)
    {
        Require(mutation.Data.CurChapterDb != null, 20155022);
        Require(!HasPendingSkillChoice(mutation), 20155023);
        TheatreChapterDb chapter = mutation.Data.CurChapterDb!;
        var config = Rows<TheatreChapterTable>().SingleOrDefault(row => row.Id == chapter.ChapterId);
        Require(config != null, 20155022);
        var nodes = Rows<TheatreNodeTable>().Where(row => row.ChapterId == chapter.ChapterId).OrderBy(row => row.Id).ToList();
        Require(nodes.Count > 0 && mutation.State.NodeCursor <= nodes.Count, 20155017);
        if (mutation.State.NodeCursor == nodes.Count)
        {
            if (!mutation.Data.PassChapterId.Contains(chapter.ChapterId)) mutation.Data.PassChapterId.Add(chapter.ChapterId);
            if (!mutation.State.CompletedChapterIds.Contains(chapter.ChapterId)) mutation.State.CompletedChapterIds.Add(chapter.ChapterId);
            // The Lua ChapterSettle receiver increments the current chapter by exactly one.
            var next = Rows<TheatreChapterTable>().SingleOrDefault(row => row.Id == chapter.ChapterId + 1 && row.GroupId == config!.GroupId);
            if (next == null)
            {
                SettleRun(mutation);
                return;
            }
            mutation.Push(new NotifyTheatreChapterSettle { SettleData = new()
            {
                PassChapterId = mutation.Data.PassChapterId.ToList(),
                PassEventRecord = Clone(mutation.Data.PassEventRecord),
                EndingRecord = mutation.Data.EndingRecord.ToList()
            } });
            chapter = new TheatreChapterDb { ChapterId = next.Id };
            mutation.Data.CurChapterDb = chapter;
            mutation.State.NodeCursor = 0;
            InitializeChapterRecruit(mutation);
            nodes = Rows<TheatreNodeTable>().Where(row => row.ChapterId == next.Id).OrderBy(row => row.Id).ToList();
            Require(nodes.Count > 0, 20155017);
        }
        var node = nodes[mutation.State.NodeCursor];
        // LOCAL generation: authored rows are sequential; each Type/Param pair is a slot.
        // Random rows offer each positive expectation-adjusted reward bucket, capped only by authored MaxRandom.
        // A stable seed plus persisted complete offers prevents retries/relogs from rerolling content.
        var random = new Random(unchecked((int)mutation.State.RunId * 397 ^ node.Id));
        var slots = new List<TheatreSlot>();
        if (node.IsRandom == 1)
        {
            var rewards = NodeRewardWeights(mutation, node).Where(pair => pair.Weight > 0).ToList();
            Require(rewards.Count > 0, 20155017);
            int count = node.MaxRandom > 0 ? Math.Min(node.MaxRandom.Value, rewards.Count) : rewards.Count;
            for (int index = 0; index < count; index++)
            {
                var reward = PickNodeWeighted(rewards, pair => pair.Weight, random);
                rewards.Remove(reward);
                var candidates = Rows<TheatreStageTable>().Where(stage => stage.ChapterId == node.ChapterId && stage.IsRandom == 1
                    && stage.Weight > 0 && stage.StageId.Any(id => id > 0)
                    && mutation.Data.Skills.Count >= stage.SelectSkillBegin.GetValueOrDefault()
                    && (!stage.SelectSkillEnd.HasValue || mutation.Data.Skills.Count <= stage.SelectSkillEnd.Value)).ToList();
                Require(candidates.Count > 0, 20155021);
                var stage = PickNodeWeighted(candidates, candidate => candidate.Weight, random);
                var slot = NewBattleSlot(mutation, stage, 4);
                slot.RewardType = reward.Type;
                slots.Add(slot);
            }
        }
        else
        {
            for (int index = 0; index < node.Type.Count; index++)
            {
                int type = node.Type[index];
                if (type == 0) continue;
                Require(index < node.Param.Count, 20155029);
                string parameter = node.Param[index];
                var slot = new TheatreSlot { SlotId = AllocateUid(mutation), SlotType = type };
                if (type == 6)
                {
                    Require(!string.IsNullOrWhiteSpace(parameter), 20155029);
                    slot.StoryId = parameter;
                }
                else
                {
                    Require(int.TryParse(parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out int configId) && configId > 0, 20155029);
                    if (type == 1) InitialiseEventSlot(mutation, slot, configId);
                    else if (type == 2) InitialiseShopSlot(mutation, slot, configId);
                    else if (type is 3 or 4 or 5)
                    {
                        // Explicit authored references may reuse another chapter's stage plan (including SP).
                        var stage = Rows<TheatreStageTable>().SingleOrDefault(row => row.Id == configId);
                        Require(stage != null, 20155021);
                        slot = NewBattleSlot(mutation, stage!, type);
                        var rewards = NodeRewardWeights(mutation, node).Where(pair => pair.Weight > 0).ToList();
                        if (rewards.Count > 0) slot.RewardType = PickNodeWeighted(rewards, pair => pair.Weight, random).Type;
                    }
                    else Require(false, 20155025);
                }
                slots.Add(slot);
            }
        }
        Require(slots.Count > 0, 20155017);
        // Battle previews index the faction immediately; freeze it before publishing the offer.
        foreach (var slot in slots.Where(slot => slot.RewardType == 1))
            slot.PowerId = DrawSkillPower(mutation);
        chapter.CurNodeDb = new TheatreNodeData { NodeId = node.Id, Slots = slots };
        mutation.State.NodeCompletionPending = false;
        mutation.State.ShopSkillOpened = false;
        mutation.State.PendingSkillShopType = 0;
        mutation.Data.MultiTeamDatas.Clear();
        mutation.Push(new NotifyTheatreAddNode { ChapterId = chapter.ChapterId, NodeId = node.Id, Slots = Clone(slots) });
    }

    private static TheatreSlot NewBattleSlot(Mutation mutation, TheatreStageTable stage, int type)
    {
        var stages = stage.StageId.Where(id => id > 0).ToList();
        Require(stages.Count > 0 && stage.StageCount == stages.Count, 20155021);
        return new TheatreSlot { SlotId = AllocateUid(mutation), SlotType = type, TheatreStageId = stage.Id, StageIds = stages };
    }

    private static List<(int Type, double Weight)> NodeRewardWeights(Mutation mutation, TheatreNodeTable node)
    {
        var chapter = Rows<TheatreChapterTable>().Single(row => row.Id == node.ChapterId);
        var factors = Rows<TheatreFactorTable>().ToDictionary(row => row.Id, row => row.Factor);
        // LOCAL: base weight + positive deficit * matching TheatreFactor; absent LvWeight is zero.
        // Currency expectations are measured in authored chapter payout units, not arbitrary inventory units.
        return
        [
            (1, node.SkillWeight.GetValueOrDefault() + Math.Max(0, node.SkillExpect.GetValueOrDefault() - mutation.Data.Skills.Count) * factors[1]),
            (2, Math.Max(0, node.LvExpect.GetValueOrDefault() - mutation.Data.CurRoleLv) * factors[2]),
            (3, node.DecorationWeight.GetValueOrDefault() + Math.Max(0, node.DecorationExpect.GetValueOrDefault() - (double)mutation.Data.DecorationCoin / Math.Max(1, chapter.DecorationPoint)) * factors[3]),
            (4, node.FavorWeight.GetValueOrDefault() + Math.Max(0, node.FavorExpect.GetValueOrDefault() - (double)mutation.Data.FavorCoin / Math.Max(1, chapter.FavorPoint)) * factors[4])
        ];
    }

    internal static T PickNodeWeighted<T>(IReadOnlyList<T> candidates, Func<T, double> weight, Random random)
    {
        double total = candidates.Sum(weight);
        Require(candidates.Count > 0 && double.IsFinite(total) && total > 0, 20155029);
        double draw = random.NextDouble() * total;
        foreach (T candidate in candidates)
        {
            double value = weight(candidate);
            Require(double.IsFinite(value) && value >= 0, 20155029);
            if (draw < value) return candidate;
            draw -= value;
        }
        return candidates.Last(candidate => weight(candidate) > 0);
    }
}
