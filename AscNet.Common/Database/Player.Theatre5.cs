using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("theatre5")]
    public PlayerTheatre5State Theatre5 { get; set; } = new();
}

public sealed class PlayerTheatre5State
{
    public Theatre5DataDb Data { get; set; } = new();
    public long Epoch { get; set; }
    public long RunId { get; set; }
    public long PvpRunId { get; set; }
    public long PveRunId { get; set; }
    public long NextMutationId { get; set; }
    public long NextAttemptId { get; set; }
    public Theatre5Attempt? PvpAttempt { get; set; }
    public Theatre5Attempt? PveAttempt { get; set; }
    public Theatre5AutoChessNpcData? PvpOpponent { get; set; }
    public int PvpOpponentPlayerId { get; set; }
    public int PvpOpponentRobotId { get; set; }
    public int PvpOpponentRating { get; set; }
    public int? PvpInitialRating { get; set; }
    public double PvpProcessRating { get; set; }
    public int PvpWinCount { get; set; }
    public int PvpLoseCount { get; set; }
    public int PvpDrawCount { get; set; }
    public int PvpContinueWin { get; set; }
    public int PvpContinueLose { get; set; }
    public Theatre5PendingMutation? PendingMutation { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5RequestReceipt> RequestReceipts { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> TaskProgress { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> CharacterWinCounts { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ChapterPassCounts { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, HashSet<int>> PveHandledEvents { get; set; } = [];
    public HashSet<int> ClaimedTaskIds { get; set; } = [];
    public HashSet<string> ClaimedRewards { get; set; } = [];
}

// Frozen native authorization belongs to the mode, never Session.fight. Entry bytes
// are serialized DlcSingleEnterFightResponse, preserving both actors and their maps.
// The driver's default convention pack sets IgnoreExtraElements(false), so a document
// written before the projection cache was removed still carries the orphaned element
// and must keep deserializing.
[BsonIgnoreExtraElements]
public sealed class Theatre5Attempt
{
    public long Epoch { get; set; }
    public long RunId { get; set; }
    public long AttemptId { get; set; }
    public int Mode { get; set; }
    public int WorldId { get; set; }
    public int LevelId { get; set; }
    public int ChapterId { get; set; }
    public int RoundNum { get; set; }
    public string RoomId { get; set; } = string.Empty;
    public string FightUid { get; set; } = string.Empty;
    public int Seed { get; set; }
    public long StartedAt { get; set; }
    public long OpponentPlayerId { get; set; }
    public int OpponentRobotId { get; set; }
    public byte[] EntryResponse { get; set; } = [];
    public byte[] OpponentSnapshot { get; set; } = [];
    public List<Theatre5Effect> Effects { get; set; } = [];
    public bool Settled { get; set; }
    public bool CheckFailed { get; set; }
    public bool Interrupted { get; set; }
    public string SettleRequestKey { get; set; } = string.Empty;
    public byte[]? SettleResponse { get; set; }
}

public sealed class Theatre5PendingMutation
{
    public string RequestKey { get; set; } = string.Empty;
    public PlayerTheatre5State Outcome { get; set; } = new();
    public List<Theatre5PendingRewardGrant> Grants { get; set; } = [];
    public List<Theatre5PendingPacket> Pushes { get; set; } = [];
    public List<int> ClaimedTaskIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<long, int> ShopBuyTimes { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ConditionCounters { get; set; } = [];
    public byte[]? Response { get; set; }
    public string ResponseName { get; set; } = string.Empty;
}

public sealed class Theatre5PendingRewardGrant
{
    public string ClaimKey { get; set; } = string.Empty;
    public List<Theatre5PendingGoods> Goods { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Costs { get; set; } = [];
    public int? EventCause { get; set; }
}

public sealed class Theatre5PendingGoods
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Count { get; set; }
    public List<int> Params { get; set; } = [];
}

public sealed class Theatre5PendingPacket
{
    public string Name { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
}

public sealed class Theatre5RequestReceipt
{
    public long Epoch { get; set; }
    public long RunId { get; set; }
    public int Mode { get; set; }
    public long ModeRunId { get; set; }
    public long MutationId { get; set; }
    public string RequestKey { get; set; } = string.Empty;
    public string ResponseName { get; set; } = string.Empty;
    public byte[] Response { get; set; } = [];
}
