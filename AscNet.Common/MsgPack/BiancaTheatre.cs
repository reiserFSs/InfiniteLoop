using MessagePack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class BiancaTheatreChapterDb
{
    public int ChapterId { get; set; }
    public List<BiancaTheatreStep> Steps { get; set; } = [];
    public int PassFightCount { get; set; }
    public int PassChapter { get; set; }
    public int PassNodeCount { get; set; }
}

[MessagePackObject(true)]
public sealed class BiancaTheatreStep
{
    public int Uid { get; set; }
    public int StepType { get; set; }
    public int RootUid { get; set; }
    public int Overdue { get; set; }
    public List<int> ItemIds { get; set; } = [];
    public int SelectedItemId { get; set; }
    public int IsExtraReward { get; set; }
    public List<int> TickIds { get; set; } = [];
    public int TickId { get; set; }
    public List<int> RefreshCharacterIds { get; set; } = [];
    public List<int> RecruitCharacterIds { get; set; } = [];
    public List<int> FloorIndexes { get; set; } = [];
    public int RefreshCount { get; set; }
    public int RecruitCount { get; set; }
    public int CurRefreshCount { get; set; }
    public int CurRecruitCount { get; set; }
    public BiancaTheatreNodeData? NodeData { get; set; }
    public List<BiancaTheatreFightReward> FightRewards { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class BiancaTheatreNodeData
{
    public int NodeId { get; set; }
    public List<BiancaTheatreNodeSlot> Slots { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class BiancaTheatreNodeSlot
{
    public int SlotId { get; set; }
    public int SlotType { get; set; }
    public int Selected { get; set; }
    public int FightId { get; set; }
    public int FightTemplateId { get; set; }
    public List<BiancaTheatreFightReward> NodeRewards { get; set; } = [];
    public List<int> PassedStageIds { get; set; } = [];
    public int EventId { get; set; }
    public int CurStepId { get; set; }
    public List<int> PassedStepId { get; set; } = [];
    public int ShopId { get; set; }
    public List<BiancaTheatreShopItem> ShopItems { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class BiancaTheatreShopItem
{
    public int Uid { get; set; }
    public int ItemType { get; set; }
    public int ItemId { get; set; }
    public int TicketId { get; set; }
    public int Price { get; set; }
    public int IsBuy { get; set; }
    public int IsLock { get; set; }
    public int DiscountPrice { get; set; }
}

[MessagePackObject(true)]
public sealed class BiancaTheatreFightReward
{
    public int Uid { get; set; }
    public int RewardType { get; set; }
    public int ConfigId { get; set; }
    public int Count { get; set; }
    public int Received { get; set; }
    public int TagType { get; set; }
}

[MessagePackObject(true)]
public sealed class BiancaTheatreCharacter
{
    public int CharacterId { get; set; }
    public int Level { get; set; }
    public int IsDecay { get; set; }
}

[MessagePackObject(true)]
public sealed class BiancaTheatreItem
{
    public int Uid { get; set; }
    public int ItemId { get; set; }
}

[MessagePackObject(true)]
public sealed class BiancaTheatreTeamData
{
    public int TeamIndex { get; set; }
    public int CaptainPos { get; set; }
    public int FirstFightPos { get; set; }
    public List<int> CardIds { get; set; } = [];
    public List<int> RobotIds { get; set; } = [];
    public int EnterCgIndex { get; set; }
    public int SettleCgIndex { get; set; }
}

[MessagePackObject(true)]
public sealed class BiancaTheatreTeamRecord
{
    public int TeamId { get; set; }
    public List<int> EndRecords { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class BiancaTheatreAchievementRecord
{
    public int NeedCountId { get; set; }
}

[MessagePackObject(true)]
public sealed class BiancaTheatreSettleData
{
    public int EndId { get; set; }
    public int NodeCount { get; set; }
    public int FightNodeCount { get; set; }
    public int TotalCharacterLevel { get; set; }
    public int TotalItemCount { get; set; }
    public int ChapterCount { get; set; }
    public int NodeCountScore { get; set; }
    public int FightNodeCountScore { get; set; }
    public int TotalCharacterLevelScore { get; set; }
    public int TotalItemCountScore { get; set; }
    public int ChapterCountScore { get; set; }
    public int TotalScore { get; set; }
    public string EndFactor { get; set; } = string.Empty;
    public string DifficultyFactor { get; set; } = string.Empty;
    public int TotalExp { get; set; }
    public int OutItemCount { get; set; }
    public int TeamId { get; set; }
    public bool NewEnding { get; set; }
    public bool NewRecord { get; set; }
    public List<BiancaTheatreCharacter> Characters { get; set; } = [];
    public List<BiancaTheatreItem> Items { get; set; } = [];
    public List<int> UnlockPowerFavorIds { get; set; } = [];
    public List<int> PassChapterIds { get; set; } = [];
    public List<int> UnlockDifficultyId { get; set; } = [];
    public List<int> UnlockItemId { get; set; } = [];
    public List<int> UnlockTeamId { get; set; } = [];
    public List<BiancaTheatreTeamRecord> TeamRecords { get; set; } = [];
    public int HistoryTotalItemCount { get; set; }
    public int HistoryTotalPassFightNodeCount { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> HistoryItemObtainRecords { get; set; } = [];
}
