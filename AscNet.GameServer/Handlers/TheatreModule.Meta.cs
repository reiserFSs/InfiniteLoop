using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.theatre;

namespace AscNet.GameServer.Handlers;

internal static partial class TheatreModule
{
    private static int MetaConfig(string key) => Rows<TheatreConfigTable>().Single(row => row.Key == key).Value;
    private static int MetaFloor(decimal value) => value >= 0 ? checked((int)decimal.Floor(value))
        : throw new InvalidDataException("Negative Theatre economic value.");

    internal static void InitializeMeta(Mutation m)
    {
        InitializeMetaTasks(m);
        // Level-zero rows describe the unpurchased state, not an automatic decoration grant.
        foreach (var group in Rows<TheatreDecorationTable>().GroupBy(row => row.DecorationId))
            if (!group.Any(row => m.Data.Decorations.Contains(row.Id)))
                m.Data.Decorations.Add(group.Single(row => (row.Lv ?? 0) == 0).Id);
        foreach (var power in Rows<TheatrePowerConditionTable>())
        {
            if (!IsConditionSatisfied(m, power.ConditionId ?? 0)) continue;
            if (!m.Data.UnlockPowerIds.Contains(power.Id)) m.Data.UnlockPowerIds.Add(power.Id);
            var initial = Rows<TheatrePowerFavorTable>().Single(row => row.PowerId == power.Id && (row.Lv ?? 0) == 0);
            if (!m.Data.UnlockPowerFavorIds.Contains(initial.Id)) m.Data.UnlockPowerFavorIds.Add(initial.Id);
        }
    }

    [RequestPacketHandler("TheatreDecorationUpgradeRequest")]
    public static void TheatreDecorationUpgradeRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreDecorationUpgradeRequest, TheatreDecorationUpgradeResponse>(session, packet, (m, request, _) =>
        {
            var current = Rows<TheatreDecorationTable>().SingleOrDefault(row => row.Id == request.DecorationId);
            Require(current != null && m.Data.Decorations.Contains(request.DecorationId), 20155011);
            var next = Rows<TheatreDecorationTable>().SingleOrDefault(row => row.DecorationId == current!.DecorationId
                && (row.Lv ?? 0) == (current.Lv ?? 0) + 1);
            Require(next != null, 20155013);
            Require(IsConditionSatisfied(m, MetaConfig("DecorationConditionId"))
                && IsConditionSatisfied(m, current!.ConditionId ?? 0), 20155012);
            Require(current!.UpgradeCostItemId > 0 && current.UpgradeCostCount > 0, 20155013);
            m.Cost(current.UpgradeCostItemId!.Value, current.UpgradeCostCount!.Value);
            m.Data.Decorations.Remove(current.Id);
            m.Data.Decorations.Add(next!.Id);
            RecordProgress(m, 73005);
            RecordProgress(m, 73015);
            InitializeMeta(m);
        });

    [RequestPacketHandler("TheatrePowerFavorUpgradeRequest")]
    public static void TheatrePowerFavorUpgradeRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatrePowerFavorUpgradeRequest, TheatrePowerFavorUpgradeResponse>(session, packet, (m, request, _) =>
        {
            var current = Rows<TheatrePowerFavorTable>().SingleOrDefault(row => row.Id == request.PowerFavorId);
            Require(current != null && m.Data.UnlockPowerIds.Contains(current.PowerId)
                && m.Data.UnlockPowerFavorIds.Contains(request.PowerFavorId)
                && IsConditionSatisfied(m, MetaConfig("FavorConditionId")), 20155014);
            var next = Rows<TheatrePowerFavorTable>().SingleOrDefault(row => row.PowerId == current!.PowerId
                && (row.Lv ?? 0) == (current.Lv ?? 0) + 1);
            Require(next != null && !m.Data.UnlockPowerFavorIds.Contains(next.Id) && current!.UpgradeCost > 0, 20155015);
            m.Cost(96103, current!.UpgradeCost!.Value);
            m.Data.UnlockPowerFavorIds.Add(next!.Id);
            RecordProgress(m, 73007);
        });

    [RequestPacketHandler("TheatreGetPowerFavorRewardRequest")]
    public static void TheatreGetPowerFavorRewardRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreGetPowerFavorRewardRequest, TheatreGetPowerFavorRewardResponse>(session, packet, (m, request, response) =>
        {
            var row = Rows<TheatrePowerFavorTable>().SingleOrDefault(row => row.Id == request.PowerFavorId);
            Require(row != null && (row.Lv ?? 0) > 0 && m.Data.UnlockPowerFavorIds.Contains(request.PowerFavorId)
                && !m.Data.EffectPowerFavorIds.Contains(request.PowerFavorId), 20155016);
            if (row!.RewardType.Count != row.RewardParam.Count)
                throw new InvalidDataException("Theatre favor reward arity mismatch.");
            for (int i = 0; i < row.RewardType.Count; i++)
            {
                switch (row.RewardType[i])
                {
                    case 1: response.RewardGoodsList.AddRange(GrantMetaReward(m, row.RewardParam[i])); break;
                    case 5: UnlockKeepsake(m, row.RewardParam[i]); break;
                    // Skill/effect owner consumes claimed rows; purchasing a level alone does not activate rewards.
                    case 2: case 3: case 4: break;
                    default: throw new InvalidDataException($"Unknown Theatre favor reward {row.RewardType[i]}.");
                }
            }
            m.Data.EffectPowerFavorIds.Add(row.Id);
        });

    internal static void UnlockKeepsake(Mutation m, int keepsakeId)
    {
        if (m.Data.Keepsakes.Any(row => row.KeepsakeId == keepsakeId)) return;
        var initial = Rows<TheatreItemTable>().Where(row => row.Type == 1 && row.KeepsakeId == keepsakeId)
            .OrderBy(row => row.Lv).FirstOrDefault() ?? throw new InvalidDataException($"Unknown Theatre keepsake {keepsakeId}.");
        var keepsake = new TheatreKeepsake { KeepsakeId = keepsakeId, Lv = initial.Lv ?? 0 };
        m.Data.Keepsakes.Add(keepsake);
        m.Push(new NotifyUnlockKeepsake { Keepsake = Clone(keepsake) });
    }

    private static List<RewardGoods> GrantMetaReward(Mutation m, int rewardId)
    {
        var goods = RewardHandler.GetRewardGoods(rewardId);
        m.Grant(rewardId);
        return goods.Select(row => new RewardGoods
        {
            Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
            RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidDataException($"Unsupported Theatre reward {row.Id}."))
        }).ToList();
    }

    [RequestPacketHandler("TheatreSettleAdventureRequest")]
    public static void TheatreSettleAdventureRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreSettleAdventureRequest, TheatreSettleAdventureResponse>(session, packet, (m, _, response) =>
        {
            Require(m.Data.CurChapterDb != null && m.State.SettledRunId != m.State.RunId, 20155005);
            response.SettleData = SettleRunCore(m, true, false);
        });

    internal static TheatreAdventureSettleData SettleRun(Mutation m, bool abandoned = false) => SettleRunCore(m, abandoned, true);

    // Fresh-login presentation only: the immediately preceding snapshot restored the old
    // adventure required by the native settle UI. Never re-grant or reactivate durable state.
    internal static void SendRecoveredSettlement(Session session)
    {
        var state = session.player.Theatre;
        if (state.Data.CurChapterDb is null && state.SettlementRecoveryPending
            && state.LastRunData is not null && state.LastSettle is not null)
            session.SendPush(new NotifyTheatreAdventureSettle { SettleData = Clone(state.LastSettle) });
    }

    private static TheatreAdventureSettleData SettleRunCore(Mutation m, bool abandoned, bool push)
    {
        Require(m.Data.CurChapterDb != null && m.State.SettledRunId != m.State.RunId, 20155005);
        var ending = Rows<TheatreEndingTable>().Where(row => EndingMatches(m, row))
            .OrderByDescending(row => row.Priority).FirstOrDefault()
            ?? throw new InvalidDataException("No eligible Theatre ending.");
        var difficulty = Rows<TheatreDifficultyTable>().Single(row => row.Id == m.Data.DifficultyId);
        var factors = Rows<TheatreSettleFactorTable>().ToDictionary(row => row.Id, row => (decimal)(row.Factor ?? 0));
        var result = new TheatreAdventureSettleData
        {
            Ending = ending.Id,
            SettleNodeCount = m.State.RunNodeCount,
            SettleFightCount = m.State.RunFightCount,
            SettleEventCount = m.State.RunEventCount,
            SettleBossCount = m.State.RunBossCount,
            SettleLeftReopenCount = Math.Max(0, checked(difficulty.ReopenCount + GetModifiers(m).ReopenBonus - m.Data.ReopenCount))
        };
        // LOCAL settlement policy: floor each of all five count*coefficient terms, then sum.
        // The authored fifth coefficient is blank and contributes zero, never an invented bonus.
        // An empty abandoned run yields zero. Difficulty scales pending currency once, not score;
        // node rewards already applied their inspiration/cadenza multipliers using decimal arithmetic.
        if (result.SettleNodeCount > 0)
        {
            result.SettleNodeCountPoint = MetaFloor(result.SettleNodeCount * factors[1]);
            result.SettleFightCountPoint = MetaFloor(result.SettleFightCount * factors[2]);
            result.SettleEventCountPoint = MetaFloor(result.SettleEventCount * factors[3]);
            result.SettleBossCountPoint = MetaFloor(result.SettleBossCount * factors[4]);
            result.SettleLeftReopenCountPoint = MetaFloor(result.SettleLeftReopenCount * factors[5]);
        }
        result.TotalPoint = checked(result.SettleNodeCountPoint + result.SettleFightCountPoint + result.SettleEventCountPoint
            + result.SettleBossCountPoint + result.SettleLeftReopenCountPoint);
        result.NewRecord = result.TotalPoint > m.State.BestScore;
        m.State.BestScore = Math.Max(m.State.BestScore, result.TotalPoint);
        result.FavorCoin = MetaFloor(m.Data.FavorCoin * (decimal)difficulty.RewardFactor);
        result.DecorationCoin = MetaFloor(m.Data.DecorationCoin * (decimal)difficulty.RewardFactor);
        GrantSettlementCurrency(m, 96103, result.FavorCoin);
        GrantSettlementCurrency(m, 96102, result.DecorationCoin);
        foreach (int chapter in m.State.CompletedChapterIds)
            if (!m.Data.PassChapterId.Contains(chapter)) m.Data.PassChapterId.Add(chapter);
        // The source ending table has no abandoned/completed discriminator: only actual run
        // chapter/event achievements select its highest-priority row, including the base ending.
        result.NewEnding = !m.Data.EndingRecord.Contains(ending.Id);
        if (result.NewEnding) m.Data.EndingRecord.Add(ending.Id);
        RecordProgress(m, 73001, 1, ending.Id);
        RecordProgress(m, 73014, 1, ending.Id);
        InitializeMeta(m);
        result.UnlockPowerFavorIds = [.. m.Data.UnlockPowerFavorIds];
        result.PassChapterId = [.. m.Data.PassChapterId];
        result.PassEventRecord = Clone(m.Data.PassEventRecord);
        result.EndingRecord = [.. m.Data.EndingRecord];
        m.State.LastRunData = Clone(m.Data);
        m.State.LastSettle = Clone(result);
        m.State.SettledRunId = m.State.RunId;
        m.State.SettlementRecoveryPending = true;
        ResetSettledRun(m);
        if (push) m.Push(new NotifyTheatreAdventureSettle { SettleData = Clone(result) });
        return result;
    }

    private static bool EndingMatches(Mutation m, TheatreEndingTable row)
    {
        if (row.Type.Count != row.Param.Count) throw new InvalidDataException("Theatre ending requirement arity mismatch.");
        for (int i = 0; i < row.Type.Count; i++)
            if (!(row.Type[i] switch
            {
                1 => row.Param[i] == 0,
                2 => m.State.RunEventIds.Contains(row.Param[i]),
                3 => m.State.CompletedChapterIds.Contains(row.Param[i]),
                _ => false
            })) return false;
        return true;
    }

    private static void GrantSettlementCurrency(Mutation m, int itemId, int count)
    {
        if (count == 0) return;
        // LOCAL producer assignment: authored guide10008 owns causes92004/92008;
        // tasks use92004 and settlement uses92008. Shared receipts count only its exact target items.
        m.Grant(new RewardGrant($"theatre:settle:{m.Session.player.PlayerData.Id}:{m.State.RunId}:{itemId}",
            [new RewardGoodsTable { Id = itemId, TemplateId = itemId, Count = count }], EventCause: 92008));
    }
}
