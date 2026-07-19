using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class PassportState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    [BsonElement("passport_infos")]
    public List<PassportStateInfo> PassportInfos { get; set; } = new();

    [BsonElement("last_time_base_info")]
    public PassportStateBaseInfo LastTimeBaseInfo { get; set; } = new();

    [BsonElement("is_get_supply_reward")]
    public bool IsGetSupplyReward { get; set; }

    [BsonElement("is_activate_regression_task")]
    public bool IsActivateRegressionTask { get; set; }

    [BsonElement("is_activate_newbie_task")]
    public bool IsActivateNewbieTask { get; set; }
}

public sealed class PassportStateInfo
{
    [BsonElement("id")]
    public int Id { get; set; }

    [BsonElement("got_reward_list")]
    public List<int> GotRewardList { get; set; } = new();
}

public sealed class PassportStateBaseInfo
{
    [BsonElement("level")]
    public int Level { get; set; }

    [BsonElement("exp")]
    public long Exp { get; set; }
}

public partial class Player
{
    [BsonElement("passport")]
    public PassportState Passport { get; set; } = new();
}
