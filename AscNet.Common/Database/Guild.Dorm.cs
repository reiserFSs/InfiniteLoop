using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public sealed partial class Guild
{
    [BsonElement("dorm_rooms")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, GuildDormRoomState> DormRooms { get; set; } = [];
}

public sealed class GuildDormRoomState
{
    public int ThemeId { get; set; }
    public List<int> BgmIds { get; set; } = [];
    public long NextSetRoomThemeTime { get; set; }
}

public partial class GuildPlayerState
{
    public GuildDormPlayerState Dorm { get; set; } = new();
}

public sealed class GuildDormPlayerState
{
    public int CurrentCharacterId { get; set; }
    public long DailyPeriod { get; set; } = long.MinValue;
    public int DailyInteractRewardTotalTimes { get; set; }
    public int DailyInteractRewardCurTimes { get; set; }
    public int DailyInteractEarnedTimes { get; set; }
    public long LastInteractRewardAt { get; set; }
    public HashSet<long> OneTimeInteractReplyIds { get; set; } = [];
    public HashSet<int> InteractedFurnitureIds { get; set; } = [];
    public List<GuildDormRandomBoxState> RandomBoxes { get; set; } = [];
}

public sealed class GuildDormRandomBoxState
{
    public int FurnitureId { get; set; }
    public int RandomItemId { get; set; }
    public int RandomTimes { get; set; }
}
