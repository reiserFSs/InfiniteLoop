using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("theatre")]
    public PlayerTheatreState Theatre { get; set; } = new();
}

public sealed class PlayerTheatreState
{
    public TheatreData Data { get; set; } = new();
    public int NextUid { get; set; }
    public long RunId { get; set; }
    public long NextMutationId { get; set; }
    public int Mode { get; set; }
    public List<int> FrozenRoleIds { get; set; } = [];
    public List<int> FrozenOwnCharacterIds { get; set; } = [];
    public bool FrozenRolePoolInitialized { get; set; }
    public int NodeCursor { get; set; }
    public bool NodeCompletionPending { get; set; }
    public List<int> PendingSkillPowers { get; set; } = [];
    public bool ShopSkillOpened { get; set; }
    public int PendingSkillShopType { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, List<int>> EventVisitedSteps { get; set; } = [];
    public int RunNodeCount { get; set; }
    public int RunFightCount { get; set; }
    public int RunEventCount { get; set; }
    public int RunBossCount { get; set; }
    public int BestScore { get; set; }
    public List<int> CompletedChapterIds { get; set; } = [];
    public HashSet<int> RunEventIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> TaskProgress { get; set; } = [];
    public long TaskDailyPeriod { get; set; } = -1;
    public HashSet<int> DailyClaimedTaskIds { get; set; } = [];
    public TheatreFightState? Fight { get; set; }
    public long LastFightId { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Int64)]
    public uint LastFightStageId { get; set; }
    public long LastFightRunId { get; set; }
    public byte[]? LastFightSettle { get; set; }
    public int LastFightPacketId { get; set; }
    public string LastFightRequestKey { get; set; } = string.Empty;
    public TheatreAdventureSettleData? LastSettle { get; set; }
    public TheatreData? LastRunData { get; set; }
    public long SettledRunId { get; set; }
    public bool SettlementRecoveryPending { get; set; }
    public TheatrePendingMutation? PendingMutation { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, TheatreRequestReceipt> RequestReceipts { get; set; } = [];
}

public sealed class TheatrePendingMutation
{
    public string RequestKey { get; set; } = string.Empty;
    public PlayerTheatreState Outcome { get; set; } = new();
    public List<TheatrePendingRewardGrant> Grants { get; set; } = [];
    public List<TheatrePendingPacket> Pushes { get; set; } = [];
    public List<int> ClaimedTaskIds { get; set; } = [];
    public List<int> ClaimedStoryTaskIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<long, int> ShopBuyTimes { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ConditionCounters { get; set; } = [];
    public byte[]? Response { get; set; }
    public string ResponseName { get; set; } = string.Empty;
}

public sealed class TheatrePendingRewardGrant
{
    public string ClaimKey { get; set; } = string.Empty;
    public List<TheatrePendingGoods> Goods { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Costs { get; set; } = [];
    public int? EventCause { get; set; }
}

public sealed class TheatrePendingGoods
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Count { get; set; }
    public List<int> Params { get; set; } = [];
}

public sealed class TheatrePendingPacket
{
    public string Name { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
}

public sealed class TheatreRequestReceipt
{
    public long RunId { get; set; }
    public long MutationId { get; set; }
    public string RequestKey { get; set; } = string.Empty;
    public string ResponseName { get; set; } = string.Empty;
    public byte[] Response { get; set; } = [];
}

public sealed class TheatreFightState
{
    public long RunId { get; set; }
    public long FightId { get; set; }
    public long Seed { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Int64)]
    public uint StageId { get; set; }
    public int StageIndex { get; set; }
    public int NodeId { get; set; }
    public int SlotId { get; set; }
    public long StartedAt { get; set; }
    public int RebootCount { get; set; }
    public int RestartCount { get; set; }
    public byte[]? PreFightPayload { get; set; }
    public TheatreTeamData Team { get; set; } = new();
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, long> RestartReceipts { get; set; } = [];
}
