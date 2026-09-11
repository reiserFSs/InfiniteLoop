using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("theatre3")]
    public PlayerTheatre3State Theatre3 { get; set; } = new();
}

// Wire snapshot follows EN matrix/xmodule/xtheatre3/xentity/XTheatre3Activity.lua.
// Journals follow Player.BiancaTheatre.cs; gameplay state is owned by this mode.
public sealed class PlayerTheatre3State
{
    public Theatre3ActivityData Data { get; set; } = new();
    public int NextUid { get; set; }
    public long RunId { get; set; }
    public Dictionary<string, int> EffectCounters { get; set; } = [];
    public HashSet<string> AppliedEffectTriggers { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ContentUseCounts { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> EncounterUseCounts { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> SlotContentIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> EventStepEnterCounts { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> LastLotteryRewards { get; set; } = [];
    public List<int> ActiveLinkGroups { get; set; } = [];
    public List<int> CompletedChapterIds { get; set; } = [];
    public int PendingEndingId { get; set; }
    public Theatre3FightState? Fight { get; set; }
    public long LastFightId { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Int64)]
    public uint LastFightStageId { get; set; }
    public byte[]? LastFightSettle { get; set; }
    public List<int> LastDestinyCharacterIds { get; set; } = [];
    public List<int> EligibleEquipSuitIds { get; set; } = [];
    public List<int> PreviousEquipSuitIds { get; set; } = [];
    public bool DestinyTriggered { get; set; }
    public bool QuantumTipUnlocked { get; set; }
    public List<int> PassedEventStepIds { get; set; } = [];
    public int TotalFightCount { get; set; }
    public int TotalCoinSpent { get; set; }
    public int TotalActivatedSuitCount { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ItemsObtainedByQuality { get; set; } = [];
    public List<int> RunPassedNodeIds { get; set; } = [];
    public List<int> RunPassedEventStepIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> RunItemsObtainedByQuality { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> TaskProgress { get; set; } = [];
    public int RunNodeCount { get; set; }
    public int RunFightCount { get; set; }
    public int RunChapterCount { get; set; }
    public HashSet<int> RunEventIds { get; set; } = [];
    public HashSet<int> RunFightNodeIds { get; set; } = [];
    public HashSet<int> RunCharacterIds { get; set; } = [];
    public int RunMaxSuitCount { get; set; }
    public int RunMaxItemCount { get; set; }
    public int RunMaxEquipCount { get; set; }
    public List<int> RunNewEquipIds { get; set; } = [];
    public List<int> RunNewItemIds { get; set; } = [];
    public Theatre3SettleData? LastSettle { get; set; }
    public long SettledRunId { get; set; }
    public bool SettlementRecoveryPending { get; set; }
    public Theatre3PendingMutation? PendingMutation { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre3RequestReceipt> RequestReceipts { get; set; } = [];
}

public sealed class Theatre3PendingMutation
{
    public string RequestKey { get; set; } = string.Empty;
    public PlayerTheatre3State Outcome { get; set; } = new();
    public List<Theatre3PendingRewardGrant> Grants { get; set; } = [];
    public List<Theatre3PendingPacket> Pushes { get; set; } = [];
    public List<int> ClaimedTaskIds { get; set; } = [];
    public List<int> ClaimedStoryTaskIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> BiancaTaskProgress { get; set; } = [];
    public List<int> BiancaReachedChapterIds { get; set; } = [];
    public byte[]? Response { get; set; }
    public string ResponseName { get; set; } = string.Empty;
}

public sealed class Theatre3PendingRewardGrant
{
    public string ClaimKey { get; set; } = string.Empty;
    public List<Theatre3PendingGoods> Goods { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Costs { get; set; } = [];
    public int? EventCause { get; set; }
}

public sealed class Theatre3PendingGoods
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Count { get; set; }
    public List<int> Params { get; set; } = [];
}

public sealed class Theatre3PendingPacket
{
    public string Name { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
}

public sealed class Theatre3RequestReceipt
{
    public long RunId { get; set; }
    public string RequestKey { get; set; } = string.Empty;
    public string ResponseName { get; set; } = string.Empty;
    public byte[] Response { get; set; } = [];
    public List<Theatre3PendingPacket> Pushes { get; set; } = [];
    public List<Theatre3PendingRewardGrant> Grants { get; set; } = [];
}

public sealed class Theatre3FightState
{
    public long FightId { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Int64)]
    public uint StageId { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Int64)]
    public uint Seed { get; set; }
    public int FightTemplateId { get; set; }
    public int QuantumTier { get; set; } = -1;
    public int NodeId { get; set; }
    public int SlotId { get; set; }
    public int EventId { get; set; }
    public int RebootCount { get; set; }
    public int ReviveCostCount { get; set; }
    public long StartedAt { get; set; }
    public byte[]? PreFightPayload { get; set; }
    public int RestartCount { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, long> RestartReceipts { get; set; } = [];
    public List<int> ExtraWaveIds { get; set; } = [];
}
