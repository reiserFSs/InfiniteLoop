using MessagePack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.MsgPack;

// Base Theatre6 wire contracts. These are the shapes the client's Theatre6 Network
// controllers deserialize; field names are client-consumed keys and must not be renamed.
[MessagePackObject(true)] public sealed class Theatre6FileData { public int SlotId { get; set; } public int CharacterId { get; set; } public int Score { get; set; } public List<int> BuildTags { get; set; } = new(); public List<Theatre6AttrData> Attrs { get; set; } = new(); public List<Theatre6SkillData> Skills { get; set; } = new(); public List<Theatre6AttrPackData> AttrPacks { get; set; } = new(); public List<Theatre6BuffData> Buffs { get; set; } = new(); public int FashionId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6AttrData { public int AttrId { get; set; } public int Value { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6SkillData { public int SlotType { get; set; } public int Position { get; set; } public int SkillId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6AttrPackData { public int PackId { get; set; } public int Num { get; set; } }
// Archive-saved stage buff (Theatre6FileState.Buffs). Live run instances are Theatre6LiveBuffData.
[MessagePackObject(true)] public sealed class Theatre6BuffData { public int BuffId { get; set; } public int TriggerCount { get; set; } public int AddMagic { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6FileSlot { public int CharacterId { get; set; } public int SlotId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6EndGameRequest { public int ModeId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6EndGameResponse { public Theatre6SettleData? SettleData { get; set; } public Theatre6StoryModeSaveDb? StoryModeSaveDb { get; set; } public int Code { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6SaveFileRequest { public int ModeId { get; set; } public int SlotId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6SaveFileResponse { public Theatre6FileData? FileData { get; set; } public int Code { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6GiveUpSaveFileRequest { public int ModeId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6GiveUpSaveFileResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6ContinueGameRequest { public int ModeId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6ContinueGameResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6StoryModeGuideFinishedRequest { public int StoryId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6StoryModeGuideFinishedResponse { public int Code { get; set; } }
// Terminal settlement. Persisted verbatim as Theatre6RunState.SettleData and rebuilt for the
// SavedData replays, so the int-keyed pass-record maps must declare the array-of-documents
// representation: BSON cannot use integer keys as element names, and the driver throws at save
// time (before any response) instead of persisting the document.
[MessagePackObject(true)] public sealed class Theatre6SettleData { public bool IsWin { get; set; } public Theatre6FileData FileData { get; set; } = new(); public int CurHeath { get; set; } public int MaxHeath { get; set; } public int CurSan { get; set; } public int MaxSan { get; set; } public List<Theatre6FightRecord> FightRecords { get; set; } = new(); public List<RewardGoods>? RewardList { get; set; } [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> PassStageRecords { get; set; } = new(); [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] public Dictionary<int,int> PassDiffRecords { get; set; } = new(); }
[MessagePackObject(true)] public sealed class Theatre6FightRecord { public int DifficultyType { get; set; } public int FightResultType { get; set; } public int FightId { get; set; } public int MonsterId { get; set; } }
[MessagePackObject(true)] public sealed class NotifyTheatre6SettleData { public Theatre6SettleData? SettleData { get; set; } public Theatre6StoryModeSaveDb? StoryModeSaveDb { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6StoryModeSaveDb { public List<Theatre6StoryLineData> StoryLineDatas { get; set; } = new(); public List<int> StoryIds { get; set; } = new(); }
[MessagePackObject(true)] public sealed class Theatre6StoryLineData { public int StoryLineId { get; set; } public int StageIndex { get; set; } public bool IsCompletedBefore { get; set; } public bool IsBuy { get; set; } public List<int> BuyIndex { get; set; } = new(); }

[MessagePackObject(true)]
public sealed class Theatre6PlayModeSaveDb
{
    public List<int> StoryIds { get; set; } = new();
    public int TalentLevel { get; set; }
    public int TalentExp { get; set; }
}

// Login snapshot root. ActivityId gates the mode; CurrentMode selects which of the two
// independent runs the unscoped Theatre6 notifications address.
[MessagePackObject(true)]
public sealed class NotifyTheatre6ActivityData
{
    public int ActivityId { get; set; }
    public int CurrentMode { get; set; }
    public Theatre6ModeDataDb? PlayModeDataDb { get; set; }
    public Theatre6ModeDataDb? StoryModeDataDb { get; set; }
    public List<Theatre6FileData> FileDatas { get; set; } = new();
    public List<Theatre6StoryLineData> StoryLineDatas { get; set; } = new();
    public List<int> PassStageId { get; set; } = new();
    public List<int> CharacterIds { get; set; } = new();
    public Theatre6PlayModeSaveDb PlayModeSaveDb { get; set; } = new();
    public Theatre6StoryModeSaveDb StoryModeSaveDb { get; set; } = new();
    public Dictionary<int, int> PassStageRecords { get; set; } = new();
    public Dictionary<int, int> PassDiffRecords { get; set; } = new();
}

// Complete current-mode snapshot. Every key below is read by XTheatre6Model/Control/stage UI:
// CurrentRoomDataDb, StageTasks, TaskSlotData, TaskGroupId, San/MinSan/MaxSan, Health/InitHealth,
// GoldAmount, ScoreTotal, Goods, Attrs (positional: list index == AttrId), AttrPacks, Skills,
// SkillOverQueue, Buffs/DestroyedBuffs (keyed by Uid), Bgms, MessyCodes, FloorBuffUid,
// BossRoomDataDb (last boss room of the floor), IsSettle/SettleData, extra-floor confirm flags.
[MessagePackObject(true)]
public sealed class Theatre6ModeDataDb
{
    public int ModeId { get; set; }
    public int StageId { get; set; }
    public int CharacterId { get; set; }
    public int FashionId { get; set; }
    public int DifficultyId { get; set; }
    public int StoryLineId { get; set; }
    public int CurFloorIdx { get; set; }
    public Theatre6RoomDataDb? CurrentRoomDataDb { get; set; }
    public Theatre6RoomDataDb? BossRoomDataDb { get; set; }
    public int San { get; set; }
    public int MinSan { get; set; }
    public int MaxSan { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int InitHealth { get; set; }
    public int GoldAmount { get; set; }
    public int ScoreTotal { get; set; }
    public List<Theatre6GoodsData> Goods { get; set; } = new();
    public List<Theatre6AttrData> Attrs { get; set; } = new();
    public Dictionary<int, Theatre6AttrPackData> AttrPacks { get; set; } = new();
    public List<Theatre6SkillData> Skills { get; set; } = new();
    public List<int> SkillOverQueue { get; set; } = new();
    public Dictionary<int, Theatre6LiveBuffData> Buffs { get; set; } = new();
    public Dictionary<int, Theatre6LiveBuffData> DestroyedBuffs { get; set; } = new();
    public Dictionary<int, Theatre6BgmData> Bgms { get; set; } = new();
    public Dictionary<int, int> MessyCodes { get; set; } = new();
    public int CurCodeId { get; set; }
    public List<int> FloorBuffUid { get; set; } = new();
    public Dictionary<int, Theatre6TaskData> StageTasks { get; set; } = new();
    public List<Theatre6TaskSlotData> TaskSlotData { get; set; } = new();
    public int TaskGroupId { get; set; }
    public bool WaitingExFloorConfirm { get; set; }
    public bool HasClearedBeforeExFloor { get; set; }
    public bool IsSettle { get; set; }
    public Theatre6SettleData? SettleData { get; set; }
}

// Unscoped mode switch. Most PvE deltas carry no ModeId, so CurrentMode must be correct first.
[MessagePackObject(true)]
public sealed class NotifyTheatre6ModeChange
{
    public int ModeId { get; set; }
}
