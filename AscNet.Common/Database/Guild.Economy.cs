using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public sealed partial class Guild
{
    public long EconomyDay { get; set; } = long.MinValue;
    public long EconomyWeek { get; set; } = long.MinValue;
    public int ContributeLeft { get; set; }
    public int Build { get; set; }
    public int GiftContribute { get; set; }
    public int GiftGuildLevel { get; set; }
    public int TalentPoint { get; set; }
    public int TalentPointFromBuild { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Talents { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<long, int> ContributionDays { get; set; } = [];
    public List<GuildContributionDay> MemberContributionDays { get; set; } = [];
    public int ShopCoin { get; set; }
    public List<int> HeadPortraits { get; set; } = [];
    public List<int> DormThemes { get; set; } = [];
    public List<int> DormBgms { get; set; } = [];
    public int MaintainState { get; set; }
    public long EmergenceTime { get; set; }
    public List<GuildWishState> Wishes { get; set; } = [];
    public int NextWishSequence { get; set; }
}

public sealed class GuildContributionDay
{
    public long PlayerId { get; set; }
    public long Day { get; set; }
    public int Count { get; set; }
}

public sealed class GuildWishState
{
    public long PlayerId { get; set; }
    public int Seq { get; set; }
    public int ItemId { get; set; }
    public int MaxCount { get; set; }
    public int GotCount { get; set; }
    public List<long> Donors { get; set; } = [];
}

public sealed partial class GuildPlayerState
{
    public long EconomyDay { get; set; } = long.MinValue;
    public long EconomyWeek { get; set; } = long.MinValue;
    public int WishCount { get; set; }
    public int WishContributeCount { get; set; }
    public List<int> GiftLevelsGot { get; set; } = [];
    public int GiftGuildGot { get; set; }
    public bool HasContributeReward { get; set; }
    public int PendingContributeReward { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> GuildShopBuyTimes { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, long> GuildShopResetPeriods { get; set; } = [];
}
