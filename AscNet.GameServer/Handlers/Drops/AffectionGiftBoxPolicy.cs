using AscNet.Common;
using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.trust;

namespace AscNet.GameServer.Handlers.Drops
{
    // The client ships no DropGroup rows for 1003, 1004, or 1005. Each box description
    // says one random gift of that tier, and the tier is the box quality. Members are
    // the favor gifts of that quality in CharacterTrustItem. No rates are published,
    // so each member is equally likely. Reward rows 1003–1005 are a different grant.
    internal static class AffectionGiftBoxPolicy
    {
        private static readonly Lazy<Dictionary<int, (IReadOnlyList<RewardGoodsTable> Pool, int Count)>> Pools = new(() =>
        {
            Dictionary<int, (IReadOnlyList<RewardGoodsTable> Pool, int Count)> pools = new();
            foreach (int sourceId in TableReaderV2.Parse<AffectionGiftBoxPolicyTable>().Select(row => row.Id).Distinct())
            {
                if (TryBuildPool(sourceId, out IReadOnlyList<RewardGoodsTable> pool, out int count))
                    pools.Add(sourceId, (pool, count));
            }
            return pools;
        });

        internal static bool Applies(int sourceId) => Pools.Value.ContainsKey(sourceId);

        internal static bool TryResolve(int sourceId, out IReadOnlyList<RewardGoodsTable> pool, out int countPerBox)
        {
            if (Pools.Value.TryGetValue(sourceId, out var resolved))
            {
                pool = resolved.Pool;
                countPerBox = resolved.Count;
                return true;
            }
            pool = Array.Empty<RewardGoodsTable>();
            countPerBox = 0;
            return false;
        }

        internal static bool TryGrant(int sourceId, int boxCount, out List<RewardGoodsTable> goods)
        {
            goods = [];
            if (!TryResolve(sourceId, out IReadOnlyList<RewardGoodsTable> pool, out int countPerBox)
                || boxCount <= 0 || (long)boxCount * countPerBox > int.MaxValue)
                return false;

            Dictionary<int, int> counts = new();
            for (int index = 0; index < boxCount; index++)
            {
                int templateId = pool[Random.Shared.Next(pool.Count)].TemplateId;
                counts[templateId] = counts.GetValueOrDefault(templateId) + countPerBox;
            }
            goods.AddRange(counts.Select(entry => new RewardGoodsTable
            {
                TemplateId = entry.Key, Count = entry.Value, Params = []
            }));
            return goods.Count > 0;
        }

        private static bool TryBuildPool(int sourceId, out IReadOnlyList<RewardGoodsTable> pool, out int countPerBox)
        {
            pool = Array.Empty<RewardGoodsTable>();
            countPerBox = 0;
            AffectionGiftBoxPolicyTable? policy = null;
            foreach (AffectionGiftBoxPolicyTable row in TableReaderV2.Parse<AffectionGiftBoxPolicyTable>())
            {
                if (row.Id != sourceId)
                    continue;
                if (policy is not null || row.Id <= 0 || row.Quality <= 0 || row.Count <= 0)
                    return false;
                policy = row;
            }
            if (policy is null)
                return false;

            HashSet<int> trustIds = TableReaderV2.Parse<CharacterTrustItemTable>().Select(row => row.Id).ToHashSet();
            List<RewardGoodsTable> candidates = TableReaderV2.Parse<ItemTable>()
                .Where(row => row.ItemType == (int)ItemType.FavorGift
                    && row.Quality == policy.Quality
                    && trustIds.Contains(row.Id))
                .OrderBy(row => row.Id)
                .Select(row => new RewardGoodsTable { TemplateId = row.Id, Count = policy.Count, Params = [] })
                .ToList();
            if (candidates.Count == 0)
                return false;

            pool = candidates;
            countPerBox = policy.Count;
            return true;
        }
    }
}
