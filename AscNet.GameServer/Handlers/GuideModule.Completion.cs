using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.reward;

namespace AscNet.GameServer.Handlers;

internal partial class GuideModule
{
    private static readonly Lazy<Dictionary<int, RewardTable>> GuideRewardTables = new(() =>
        TableReaderV2.Parse<RewardTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<HashSet<int>> GuideEquipTemplates = new(() =>
        TableReaderV2.Parse<EquipTable>().Select(row => row.Id).ToHashSet());

    private static string GuideClaimKey(int id) => $"guide:{id}";

    private static bool HasGuideRewardReceipt(Session session, int id) =>
        session.inventory.AppliedRewardClaims?.Contains(GuideClaimKey(id)) == true
        || session.character.AppliedRewardClaims?.Contains(GuideClaimKey(id)) == true;

    private static List<GuideRewardGoods> ResolveGuideReward(int rewardId)
    {
        List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
        if (!GuideRewardTables.Value.TryGetValue(rewardId, out RewardTable? reward) || goods.Count == 0
            || !goods.Select(row => row.Id).Order().SequenceEqual(reward.SubIds.Order())
            || goods.Any(row => row.Count <= 0 || !IsSupportedGuideReward(row)))
            throw new InvalidDataException($"Guide reward {rewardId} has missing or unsupported goods.");

        return goods.Select(row => new GuideRewardGoods
        {
            Id = row.Id, TemplateId = row.TemplateId, Count = row.Count
        }).ToList();
    }

    // These are the current common-guide reward categories. Both have durable document receipts
    // and absolute item/equip notifications that can be replayed without running settlement.
    private static bool IsSupportedGuideReward(RewardGoodsTable row) => RewardHandler.GetRewardType(row) switch
    {
        RewardType.Item => Inventory.IsValidClientItemId(row.TemplateId),
        RewardType.Equip => GuideEquipTemplates.Value.Contains(row.TemplateId),
        _ => false
    };

    private static RewardGoodsTable ToRewardRow(GuideRewardGoods row) =>
        new() { Id = row.Id, TemplateId = row.TemplateId, Count = row.Count, Params = [] };

    private static RewardApplicationResult? CompleteGuides(
        Session session, List<GuideCompletionEntry> entries, bool syncOnReconnect = false)
    {
        if (entries.Count == 0)
            return null;
        if (session.player.PendingGuideCompletion is not null)
            throw new InvalidOperationException("A pending guide completion must be recovered before starting another.");

        try
        {
            if (entries.Any(entry => entry.Goods is { Count: > 0 }))
            {
                session.player.PendingGuideCompletion = entries;
                session.player.SaveChecked();
                return CommitGuideCompletion(session, entries, syncOnReconnect: true);
            }
            return CommitGuideCompletion(session, entries, syncOnReconnect);
        }
        catch
        {
            // An unacknowledged replacement may have landed. Only a fresh session may replay it,
            // and disconnect must not write stale reward documents or the old GuideData over it.
            session.DisconnectProtocol(persistState: false);
            throw;
        }
    }

    internal static void ResumePendingCompletion(Session session)
    {
        if (session.player.PendingGuideCompletion is not { } pending)
            return;
        try
        {
            CommitGuideCompletion(session, pending, syncOnReconnect: true);
        }
        catch
        {
            session.DisconnectProtocol(persistState: false);
            throw;
        }
    }

    private static RewardApplicationResult? CommitGuideCompletion(
        Session session, List<GuideCompletionEntry> entries, bool syncOnReconnect)
    {
        Player player = session.player;
        List<RewardGrant> grants = entries
            .Where(entry => entry.Goods is { Count: > 0 })
            .Select(entry => new RewardGrant(GuideClaimKey(entry.GuideId), entry.Goods!.Select(ToRewardRow).ToArray()))
            .ToList();
        RewardApplicationResult? result = grants.Count == 0 ? null : RewardHandler.ApplyRewardsOnceAndPersist(grants, session);

        List<long>? previousGuides = player.PlayerData.GuideData;
        List<GuideCompletionEntry>? previousSync = player.GuideReconnectSync;
        List<GuideCompletionEntry>? pending = player.PendingGuideCompletion;
        HashSet<long> completed = new(previousGuides ?? []);
        player.PlayerData.GuideData = [.. previousGuides ?? [],
            .. entries.Select(entry => (long)entry.GuideId).Where(completed.Add)];
        if (syncOnReconnect)
            player.GuideReconnectSync = [.. previousSync ?? [], .. entries];
        player.PendingGuideCompletion = null;
        try
        {
            player.SaveChecked();
        }
        catch
        {
            player.PlayerData.GuideData = previousGuides!;
            player.GuideReconnectSync = previousSync;
            player.PendingGuideCompletion = pending;
            throw;
        }
        return result;
    }

    internal static void SyncAfterReconnect(Session session)
    {
        if (session.player.GuideReconnectSync is not { Count: > 0 } entries)
            return;

        List<RewardGoodsTable> goods = entries.SelectMany(entry => entry.Goods ?? []).Select(ToRewardRow).ToList();
        HashSet<int> items = goods.Where(row => RewardHandler.GetRewardType(row) == RewardType.Item)
            .Select(row => row.TemplateId).ToHashSet();
        HashSet<uint> equips = goods.Where(row => RewardHandler.GetRewardType(row) == RewardType.Equip)
            .Select(row => (uint)row.TemplateId).ToHashSet();
        List<Item> itemData = session.inventory.Items.Where(item => items.Contains(item.Id)).ToList();
        List<EquipData> equipData = session.character.Equips.Where(equip => equips.Contains(equip.TemplateId)).ToList();
        if (itemData.Count > 0)
            session.SendPush(new NotifyItemDataList { ItemDataList = itemData });
        if (equipData.Count > 0)
            session.SendPush(new NotifyEquipDataList { EquipDataList = equipData });
        NotifyCompletedGuides(session, entries.Select(entry => entry.GuideId));
    }

    private static void NotifyCompletedGuides(Session session, IEnumerable<int> guideIds)
    {
        foreach (int guideId in guideIds)
        {
            if (session.OpenedGuideGroupId == guideId)
                session.OpenedGuideGroupId = null;
            session.SendPush(new NotifyGuide { GuideGroupId = guideId });
        }
    }
}
