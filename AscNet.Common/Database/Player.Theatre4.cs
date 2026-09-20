using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("theatre4")]
    public PlayerTheatre4State Theatre4 { get; set; } = new();
}

// Wire snapshot follows EN matrix/xmodule/xtheatre4/xentity/XTheatre4Activity.lua and
// XTheatre4Adventure.lua. Data is the login NotifyTheatre4ActivityData payload; the extra
// fields are the mode's prepare/apply/finalize runtime contract (Theatre5 pattern without
// PvP/PvE attempts), owned by TundraCore.
public sealed class PlayerTheatre4State
{
    public Theatre4ActivityData Data { get; set; } = new();
    public long Epoch { get; set; }
    public long RunId { get; set; }
    // Per-run deterministic RNG: RandomSeed is fixed at run start, RandomCounter is the
    // durable stream position. Both ride in the committed outcome, so an offer/map already
    // generated can never be rerolled by a retried packet or a relogin.
    public int RandomSeed { get; set; }
    public long RandomCounter { get; set; }
    public long NextMutationId { get; set; }
    public Theatre4PendingMutation? PendingMutation { get; set; }
    public Theatre4ActiveEncounter? ActiveEncounter { get; set; }
    // Terminal run snapshot for the ending presentation. EndAdventure clears Data.AdventureData
    // (no active run) but retains this clone so a relog before the client acknowledges can be
    // re-sent the same NotifyTheatre4AdventureSettle.AdventureData; the next run start clears it.
    public Theatre4AdventureData? PendingSettleAdventure { get; set; }
    // Day -> frozen AdventureData clone for timeback rollback. BSON-only runtime state: the
    // wire only carries AdventureData.TracebackDatas, the per-day cost summary.
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre4AdventureData> TracebackSnapshots { get; set; } = [];
    // Retransmission receipts are transport-scoped: login/new-run boundaries clear them because
    // XNetwork packet ids can be reused by a fresh connection.
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre4RequestReceipt> RequestReceipts { get; set; } = [];
}

// Frozen fight-locate context, owned by TundraCombat. "In battle" means
// ActiveEncounter != null && SettleReceipt == null; nonterminal duplicate settle may replay
// SettleReceipt. A terminal response lives in PendingMutation until commit, then in the
// transport-scoped RequestReceipts cache while the encounter remains cleared.
public sealed class Theatre4ActiveEncounter
{
    public int Kind { get; set; }
    public int MapId { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
    public int GridId { get; set; }
    public int FateUniqueId { get; set; }
    public int FightGroupId { get; set; }
    public int FightId { get; set; }
    public int StageId { get; set; }
    public int HpPercent { get; set; }
    public List<int> FightEvents { get; set; } = [];
    public List<int> CardIds { get; set; } = [];
    public List<int> RobotIds { get; set; } = [];
    public long FightUuid { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Int64)]
    public uint Seed { get; set; }
    public long StartedAt { get; set; }
    public byte[]? PreFightPayload { get; set; }
    public int RebootCount { get; set; }
    public int AttemptCount { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, long> RestartReceipts { get; set; } = [];
    public string? SettleKey { get; set; }
    public byte[]? SettleReceipt { get; set; }
}

public sealed class Theatre4PendingMutation
{
    public string RequestKey { get; set; } = string.Empty;
    public PlayerTheatre4State Outcome { get; set; } = new();
    public List<Theatre4PendingRewardGrant> Grants { get; set; } = [];
    public List<Theatre4PendingPacket> Pushes { get; set; } = [];
    public byte[]? Response { get; set; }
    public string ResponseName { get; set; } = string.Empty;
    public List<int> ClaimedTaskIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ConditionCounters { get; set; } = [];
}

public sealed class Theatre4RequestReceipt
{
    public long Epoch { get; set; }
    public long RunId { get; set; }
    public long MutationId { get; set; }
    public string RequestKey { get; set; } = string.Empty;
    public string ResponseName { get; set; } = string.Empty;
    public byte[] Response { get; set; } = [];
}

public sealed class Theatre4PendingRewardGrant
{
    public string ClaimKey { get; set; } = string.Empty;
    public List<Theatre4PendingGoods> Goods { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Costs { get; set; } = [];
    public int? EventCause { get; set; }
}

public sealed class Theatre4PendingGoods
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Count { get; set; }
    public List<int> Params { get; set; } = [];
}

public sealed class Theatre4PendingPacket
{
    public string Name { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
}
