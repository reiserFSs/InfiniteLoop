using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.character.quality;
using AscNet.Table.V2.share.equip;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.lotto;
using AscNet.Table.V2.share.wheelchairmanual;

namespace AscNet.GameServer.Game;

internal static class LottoManager
{
    private sealed record Catalog(
        LottoTable Lotto,
        LottoPrimaryTable Primary,
        IReadOnlyDictionary<int, LottoRewardTable> Rewards);

    private static readonly Lazy<Catalog?> CurrentCatalog = new(CreateCurrentCatalog);
    private static readonly Lazy<Dictionary<int, int>> CharacterQualities = new(() =>
        TableReaderV2.Parse<CharacterQualityTable>()
            .GroupBy(row => row.CharacterId)
            .ToDictionary(group => group.Key, group => group.Min(row => row.Quality)));
    private static readonly Lazy<Dictionary<int, int>> EquipQualities = new(() =>
        TableReaderV2.Parse<EquipTable>().ToDictionary(row => row.Id, row => row.Quality));
    private static readonly Lazy<Dictionary<int, int>> ItemQualities = new(() =>
        TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id, row => row.Quality));

    internal static bool TryBuildInfo(Player player, out LottoInfoResponse.LottoInfo info)
    {
        info = new LottoInfoResponse.LottoInfo();
        Catalog? catalog = CurrentCatalog.Value;
        if (catalog is null || !TryGetProgress(player, catalog, out LottoStateInfo? progress))
            return false;

        info = new LottoInfoResponse.LottoInfo
        {
            Id = catalog.Lotto.Id,
            LottoPrimaryId = progress?.LottoPrimaryId ?? catalog.Primary.Id,
            ExtraRewardState = progress?.ExtraRewardState ?? 0,
            LottoRewards = progress?.LottoRewards.ToList() ?? [],
            LottoRecords = progress?.LottoRecords.Select(record => new LottoInfoResponse.LottoRecord
            {
                RewardGoods = ToRewardGoods(catalog.Rewards[record.RewardId]),
                LottoTime = record.LottoTime
            }).ToList() ?? []
        };
        return true;
    }

    internal static Dictionary<string, object?> BuildSelfChoicePayload(Player player)
    {
        Catalog? catalog = CurrentCatalog.Value;
        if (catalog is null || !TryGetProgress(player, catalog, out LottoStateInfo? progress))
            return new()
            {
                ["LottoPrimaryIds"] = Array.Empty<int>(),
                ["SelectedPrimaryIdToLottoId"] = new Dictionary<int, int>()
            };

        Dictionary<int, int> selected = progress is null
            ? new()
            : new() { [catalog.Primary.Id] = catalog.Lotto.Id };
        return new()
        {
            ["LottoPrimaryIds"] = new[] { catalog.Primary.Id },
            ["SelectedPrimaryIdToLottoId"] = selected
        };
    }

    private static Catalog? CreateCurrentCatalog()
    {
        try
        {
            WheelchairManualActivityTable[] activities = TableReaderV2.Parse<WheelchairManualActivityTable>()
                .Where(row => row.LottoId > 0 && row.TimeId > 0)
                .ToArray();
            if (activities.Length != 1)
                return null;

            WheelchairManualActivityTable activity = activities[0];
            LottoTable? lotto = TableReaderV2.Parse<LottoTable>()
                .SingleOrDefault(row => row.Id == activity.LottoId && row.TimeId == activity.TimeId);
            if (lotto is null || lotto.BuyTicketRuleIdList.Count == 0 || lotto.BuyTicketRuleIdList.Any(id => id <= 0))
                return null;

            LottoPrimaryTable[] primaries = TableReaderV2.Parse<LottoPrimaryTable>()
                .Where(row => row.TimeId == activity.TimeId && row.LottoIdList.Contains(lotto.Id))
                .ToArray();
            if (primaries.Length != 1)
                return null;

            Dictionary<int, LottoRewardTable> rewards = TableReaderV2.Parse<LottoRewardTable>()
                .Where(row => row.LottoId == lotto.Id)
                .ToDictionary(row => row.Id);
            if (rewards.Count == 0)
                return null;

            HashSet<int> buyTicketRules = TableReaderV2.Parse<LottoBuyTicketRuleTable>()
                .Select(row => row.Id)
                .ToHashSet();
            return lotto.BuyTicketRuleIdList.All(buyTicketRules.Contains)
                ? new Catalog(lotto, primaries[0], rewards)
                : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool TryGetProgress(Player player, Catalog catalog, out LottoStateInfo? progress)
    {
        progress = null;
        LottoState state = player.Lotto ??= new LottoState();
        if (state.Infos.Count == 0)
            return true;
        if (state.Infos.Count != 1)
            return false;

        LottoStateInfo candidate = state.Infos[0];
        if (candidate.Id != catalog.Lotto.Id || candidate.LottoPrimaryId != catalog.Primary.Id
            || candidate.ExtraRewardState is < 0 or > 1
            || candidate.LottoRewards is null || candidate.LottoRecords is null)
            return false;

        HashSet<int> claimed = candidate.LottoRewards.ToHashSet();
        if (claimed.Count != candidate.LottoRewards.Count || !claimed.All(catalog.Rewards.ContainsKey))
            return false;

        HashSet<int> recorded = candidate.LottoRecords.Select(record => record.RewardId).ToHashSet();
        if (recorded.Count != candidate.LottoRecords.Count
            || candidate.LottoRecords.Any(record => record.LottoTime < 0 || !catalog.Rewards.ContainsKey(record.RewardId))
            || !recorded.SetEquals(claimed))
            return false;

        progress = candidate;
        return true;
    }

    private static LottoInfoResponse.LottoRecord.LottoRewardGoods ToRewardGoods(LottoRewardTable reward)
    {
        int rewardType = 1;
        int quality = 0;
        if (CharacterQualities.Value.TryGetValue(reward.TemplateId, out int characterQuality))
        {
            rewardType = 2;
            quality = characterQuality;
        }
        else if (EquipQualities.Value.TryGetValue(reward.TemplateId, out int equipQuality))
        {
            rewardType = 3;
            quality = equipQuality;
        }
        else if (ItemQualities.Value.TryGetValue(reward.TemplateId, out int itemQuality))
        {
            quality = itemQuality;
        }

        return new()
        {
            RewardType = rewardType,
            TemplateId = (uint)reward.TemplateId,
            Count = reward.Count,
            Level = 0,
            Quality = quality,
            Grade = rewardType == 2 && quality > 0 ? 1 : 0,
            Breakthrough = 0,
            ConvertFrom = 0,
            IsGift = false,
            RewardMulti = 0,
            Id = 0
        };
    }
}
