using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class LottoState
{
    [BsonElement("infos")]
    public List<LottoStateInfo> Infos { get; set; } = new();
}

public sealed class LottoStateInfo
{
    [BsonElement("id")]
    public int Id { get; set; }

    [BsonElement("lotto_primary_id")]
    public int LottoPrimaryId { get; set; }

    [BsonElement("extra_reward_state")]
    public int ExtraRewardState { get; set; }

    [BsonElement("lotto_rewards")]
    public List<int> LottoRewards { get; set; } = new();

    [BsonElement("lotto_records")]
    public List<LottoStateRecord> LottoRecords { get; set; } = new();
}

public sealed class LottoStateRecord
{
    [BsonElement("reward_id")]
    public int RewardId { get; set; }

    [BsonElement("lotto_time")]
    public int LottoTime { get; set; }
}

public partial class Player
{
    [BsonElement("lotto")]
    public LottoState Lotto { get; set; } = new();
}
