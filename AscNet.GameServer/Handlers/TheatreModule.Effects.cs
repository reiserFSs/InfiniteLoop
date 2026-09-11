using AscNet.Table.V2.share.theatre;

namespace AscNet.GameServer.Handlers;

internal sealed class TheatreModifiers
{
    public int InitialCoinBonus { get; internal set; }
    public int NodeCoinBonus { get; internal set; }
    public int ShopItemCountBonus { get; internal set; }
    public int ReopenBonus { get; internal set; }
    public int RecruitBonus { get; internal set; }
    public int RefreshBonus { get; internal set; }
    public decimal ShopPriceMultiplier { get; internal set; } = 1;
    public decimal InspirationGainMultiplier { get; internal set; } = 1;
    public decimal CadenzaGainMultiplier { get; internal set; } = 1;
    public List<int> FightEventIds { get; } = [];

    // LOCAL formula: round down once, after multiplying the full nonnegative base amount.
    // These are numerical decoration operands, not a reconstruction of the retail server's rounding.
    public int ShopPrice(int basePrice) => Scale(basePrice, ShopPriceMultiplier);
    public int InspirationGain(int baseAmount) => Scale(baseAmount, InspirationGainMultiplier);
    public int CadenzaGain(int baseAmount) => Scale(baseAmount, CadenzaGainMultiplier);

    private static int Scale(int amount, decimal factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        return checked((int)decimal.Floor(amount * factor));
    }
}

internal static partial class TheatreModule
{
    internal static TheatreModifiers GetModifiers(Mutation mutation)
    {
        var result = new TheatreModifiers();
        var decorations = Rows<TheatreDecorationTable>();
        // Decorations stores table row IDs, not DecorationId or a sum of purchased levels.
        // Select the current (highest) row of each decoration if older persisted levels remain.
        var active = mutation.Data.Decorations.Distinct()
            .Select(id => decorations.Single(row => row.Id == id))
            .GroupBy(row => row.DecorationId)
            .Select(group => group.MaxBy(row => row.Lv ?? 0)!);
        foreach (var row in active)
        {
            if (row.Type.Count != row.Param.Count)
                throw new InvalidOperationException($"Theatre decoration {row.Id} has unpaired operands.");
            for (int index = 0; index < row.Type.Count; index++)
            {
                decimal value = checked((decimal)row.Param[index]);
                // LOCAL policy approved for the unavailable server implementation: independent
                // integer bonuses add; independent price/gain factors multiply. Current rows only.
                // Numeric operands, never localized display text, determine the result.
                // XTheatreConfigs.lua identifies 9/10/11 as reopen/recruit/recruit-refresh;
                // the other reachable types are grounded in TheatreDecoration's authored rows.
                switch (row.Type[index])
                {
                    case 1: result.InitialCoinBonus = checked(result.InitialCoinBonus + Integral(value)); break;
                    case 3: result.NodeCoinBonus = checked(result.NodeCoinBonus + Integral(value)); break;
                    case 4: result.ShopPriceMultiplier *= PositiveFactor(value); break;
                    case 5: result.ShopItemCountBonus = checked(result.ShopItemCountBonus + Integral(value)); break;
                    case 9: result.ReopenBonus = checked(result.ReopenBonus + Integral(value)); break;
                    case 10: result.RecruitBonus = checked(result.RecruitBonus + Integral(value)); break;
                    case 11: result.RefreshBonus = checked(result.RefreshBonus + Integral(value)); break;
                    case 12: result.InspirationGainMultiplier *= PositiveFactor(value); break;
                    case 13: result.CadenzaGainMultiplier *= PositiveFactor(value); break;
                    case 15: result.FightEventIds.Add(Integral(value)); break;
                    default:
                        throw new InvalidOperationException($"Unsupported authored theatre decoration type {row.Type[index]} on {row.Id}.");
                }
            }
        }
        return result;
    }

    private static int Integral(decimal value)
    {
        if (value < 0 || decimal.Truncate(value) != value)
            throw new InvalidOperationException("Theatre effect requires a nonnegative integral operand.");
        return checked((int)value);
    }

    private static decimal PositiveFactor(decimal value) => value > 0 ? value
        : throw new InvalidOperationException("Theatre effect requires a positive multiplier.");

    // Native FightEvent descriptors own BornMagic/EnemyBornMagic targeting. Attach this list ONCE
    // to FightData.EventIds, not to individual NpcData.EventIds. No fabricated attribute deltas.
    internal static List<int> GetFightEvents(Mutation mutation, int roleId = 0)
    {
        if (roleId != 0)
            throw new ArgumentException("Theatre fight events are global, not role-scoped.", nameof(roleId));
        var result = new List<int>();
        var skills = Rows<TheatreSkillTable>();
        foreach (int skillId in mutation.Data.Skills.Distinct())
            result.AddRange(skills.Single(row => row.Id == skillId).FightEventId);

        // Active favor (Data.EffectPowerFavorIds) is consumed by skill acquisition/upgrading:
        // XTheatreConfigs.lua PowerFavorRewardType 2 unlocks skills; 3 sets initial quality;
        // 4 sets the upgrade quality increment (highest wins); 1 rewards items, 5 keepsakes.
        // It has NO native FightEvent column. Its battle effect is already embodied in the
        // selected skill row IDs above. RewardParam must never be treated as a FightEvent ID.
        result.AddRange(GetModifiers(mutation).FightEventIds);
        if (mutation.Data.DifficultyId != 0)
        {
            var difficulty = Rows<TheatreDifficultyTable>().Single(row => row.Id == mutation.Data.DifficultyId);
            result.AddRange(difficulty.FightEventId);
            result.AddRange(difficulty.EnemyBuff);
        }

        // FightEvent.json 2240183..2240186 are effect-empty difficulty markers, checked by
        // native conditions; 2240187..2240197 carry EnemyBornMagic. Preserve both unchanged.
        // LOCAL set-union policy: one native registration per authored ID; preserve source order.
        return result.Where(id => id > 0).Distinct().ToList();
    }
}
