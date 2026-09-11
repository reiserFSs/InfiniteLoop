using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("bianca_theatre")]
    public BiancaTheatreState BiancaTheatre { get; set; } = new();
}

public sealed class BiancaTheatreState
{
    public NotifyBiancaTheatreActivityData Data { get; set; } = new();
    public int NextUid { get; set; }
    public long RunId { get; set; }
    public BiancaTheatreSettleData? LastSettleData { get; set; }
    public bool PendingSettleNotification { get; set; }
    public bool PendingAbnormalExitNotification { get; set; }
    public BiancaTheatrePendingMutation? PendingMutation { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, BiancaTheatreRequestReceipt> RequestReceipts { get; set; } = [];
    public BiancaTheatreFightState? Fight { get; set; }
    public List<BiancaTheatreTeamData> MultiTeams { get; set; } = [];
    public List<BiancaTheatreStep> QueuedSteps { get; set; } = [];
    public List<BiancaTheatreChapterDb> CompletedChapters { get; set; } = [];
    public HashSet<int> AppliedSystemEffectIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> TaskProgress { get; set; } = [];
    public int RunRecruitCount { get; set; }
    public int TotalRecruitCount { get; set; }
    public int LastEventChoiceId { get; set; }
    public int ConsecutiveEventChoices { get; set; }
    public int RunItemCount { get; set; }
    public int RunReviveCount { get; set; }
    public int RunNodeCount { get; set; }
    public int RunFightNodeCount { get; set; }
    public List<int> RunPassChapterIds { get; set; } = [];
    public List<int> PassedStageIds { get; set; } = [];
    public List<int> ReachedChapterIds { get; set; } = [];
    public int TotalCookieSpent { get; set; }
    public int CompletionCount { get; set; }
    public int BestScore { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ComboPhaseHistory { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, List<int>> HistoryPassedEventRecord { get; set; } = [];
    public List<int> SuccessfulDifficultyIds { get; set; } = [];
    public List<int> PreviousRunPassChapterIds { get; set; } = [];
    public int AbnormalExitCount { get; set; }
    public byte[]? LastFightSettle { get; set; }
    public long LastFightId { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Int64)]
    public uint LastFightStageId { get; set; }
}

public sealed class BiancaTheatreFightState
{
    [BsonRepresentation(MongoDB.Bson.BsonType.Int64)]
    public uint StageId { get; set; }
    public int FightTemplateId { get; set; }
    public int NodeId { get; set; }
    public int SlotId { get; set; }
    public long FightId { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Int64)]
    public uint Seed { get; set; }
    public int StageIndex { get; set; }
    public int RestartCount { get; set; }
    public int RebootCount { get; set; }
    public int ReviveCostCount { get; set; }
    public int EventId { get; set; }
    public int EventStepId { get; set; }
    public int EventNextStepId { get; set; }
}

public sealed class BiancaTheatrePendingMutation
{
    public string ClaimKey { get; set; } = string.Empty;
    public string RequestKey { get; set; } = string.Empty;
    public BiancaTheatreState Outcome { get; set; } = new();
    public List<BiancaTheatrePendingGoods> Goods { get; set; } = [];
    public List<BiancaTheatrePendingRewardGrant> NamedGrants { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Costs { get; set; } = [];
    public List<int> ClaimedTaskIds { get; set; } = [];
    public List<int> ClaimedStoryTaskIds { get; set; } = [];
    public List<BiancaTheatrePendingPacket> Pushes { get; set; } = [];
    public byte[]? Response { get; set; }
    public string ResponseName { get; set; } = string.Empty;
}

public sealed class BiancaTheatrePendingGoods
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Count { get; set; }
    public List<int> Params { get; set; } = [];
}

public sealed class BiancaTheatrePendingPacket
{
    public string Name { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
}

public sealed class BiancaTheatreRequestReceipt
{
    public string RequestKey { get; set; } = string.Empty;
    public string ResponseName { get; set; } = string.Empty;
    public byte[] Response { get; set; } = [];
    public List<BiancaTheatrePendingPacket> Pushes { get; set; } = [];
}

public sealed class BiancaTheatrePendingRewardGrant
{
    public string ClaimKey { get; set; } = string.Empty;
    public List<BiancaTheatrePendingGoods> Goods { get; set; } = [];
}
