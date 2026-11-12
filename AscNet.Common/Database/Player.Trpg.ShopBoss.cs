using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

/// <summary>TRPG shop purchases and the Fake Ascension world boss. Kept apart from <see cref="TrpgState"/>
/// so the shop/boss slice persists without touching exploration progress.</summary>
[BsonIgnoreExtraElements]
public sealed class TrpgShopBossState
{
    /// <summary>Count bought of one shop item inside one shop.</summary>
    [BsonIgnoreExtraElements]
    public sealed class ShopBuy
    {
        [BsonElement("shop_id")] public int ShopId { get; set; }
        [BsonElement("item_id")] public int ItemId { get; set; }
        [BsonElement("count")] public int Count { get; set; }
    }

    [BsonElement("shop_buys")] public List<ShopBuy> ShopBuys { get; set; } = new();
    /// <summary>Daily reset period (TaskModule.CurrentDailyResetPeriod) the resettable buy counts belong to.</summary>
    [BsonElement("shop_day")] public long ShopDay { get; set; }
    /// <summary>Purchases made so far; makes every purchase's reward claim key unique.</summary>
    [BsonElement("shop_serial")] public int ShopSerial { get; set; }

    /// <summary>Health the player's boss pool has lost.</summary>
    [BsonElement("boss_lose_hp")] public long BossLoseHp { get; set; }
    [BsonElement("boss_challenge_count")] public int BossChallengeCount { get; set; }
    /// <summary>Daily period the challenge count belongs to; a later period means 0 attempts used.</summary>
    [BsonElement("boss_challenge_day")] public long BossChallengeDay { get; set; }
    [BsonElement("boss_phase_rewards")] public List<int> BossPhaseRewards { get; set; } = new();
}

public partial class Player
{
    [BsonElement("trpg_shop_boss")] public TrpgShopBossState TrpgShopBoss { get; set; } = new();
}
