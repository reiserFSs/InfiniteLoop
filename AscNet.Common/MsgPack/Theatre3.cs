using MessagePack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.MsgPack;

// EN matrix/xmodule/xtheatre3: XTheatre3Control request/response consumers,
// XTheatre3Agency notification registration, xentity NotifyData readers.
// Native XFightResult (il2cpp dump.cs:67297-67298) establishes fight-map key types.

[MessagePackObject(true)]
public class Theatre3Response
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public class Theatre3ActivityData
{
    public int CurActivityId { get; set; }
    public int TotalBattlePassExp { get; set; }
    public int FirstPassFlag { get; set; }
    public int TotalAllPassCount { get; set; }
    public int FireItemCount { get; set; }
    public int DifficultyId { get; set; }
    public int MaxEnergy { get; set; }
    public int CurChapterId { get; set; }
    public int DestinyValue { get; set; }
    public int DestinyCharacterId { get; set; }
    public int QubitValueA { get; set; }
    public int QubitValueB { get; set; }
    public List<int> PassChapterIds { get; set; } = [];
    public List<int> UnlockItemId { get; set; } = [];
    public List<int> UnlockEquipId { get; set; } = [];
    public List<int> UnlockDifficultyId { get; set; } = [];
    public List<int> UnlockStrengthTree { get; set; } = [];
    public List<int> AchievementRewards { get; set; } = [];
    public List<int> EndingRecord { get; set; } = [];
    public List<int> GetRewardIds { get; set; } = [];
    public List<int> PassEventFightNodes { get; set; } = [];
    public List<Theatre3Character> Characters { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, List<int>> PassDifficultyRecords { get; set; } = [];
    public Theatre3TeamData? CurTeamData { get; set; }
    public List<Theatre3EquipPos> EquipPos { get; set; } = [];
    public List<Theatre3Item> Items { get; set; } = [];
    public bool ChapterSwitch { get; set; }
    public Theatre3ChapterDb? CurChapterDb { get; set; }
    public List<Theatre3Equip> Equips { get; set; } = [];
    public List<Theatre3FightRecord> FightRecords { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3Character
{
    public int CharacterId { get; set; }
    public int Level { get; set; }
    public int Exp { get; set; }
    public int ExpTemp { get; set; }
    public List<int> EndingIds { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3TeamData
{
    public int CaptainPos { get; set; }
    public int FirstFightPos { get; set; }
    public int EnterCgIndex { get; set; }
    public int SettleCgIndex { get; set; }
    public List<int> CardIds { get; set; } = [];
    public List<int> RobotIds { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3EquipPos
{
    public int PosId { get; set; }
    public int ColorId { get; set; }
    public int CardId { get; set; }
    public int RobotId { get; set; }
    public int Capacity { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3EquipPosInfo
{
    public int Pos { get; set; }
    public int ColorId { get; set; }
    public int CardId { get; set; }
    public int RobotId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3Item
{
    public int Uid { get; set; }
    public int ItemId { get; set; }
    public int Live { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3Equip
{
    public int Pos { get; set; }
    public int EquipId { get; set; }
    public int SuitId { get; set; }
    public int PassFightCount { get; set; }
    public int PassBossFightCount { get; set; }
    public bool QubitActive { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3ChapterDb
{
    public int ChapterId { get; set; }
    public int ConnectChapterId { get; set; }
    public int PassChapter { get; set; }
    public int PassNodeCount { get; set; }
    public int PassFightCount { get; set; }
    public int PassShopCount { get; set; }
    public int PassEventCount { get; set; }
    public List<int> PassNodeIds { get; set; } = [];
    public List<Theatre3Step> Steps { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3Step
{
    public int Uid { get; set; }
    public int RootUid { get; set; }
    public int StepType { get; set; }
    public int Overdue { get; set; }
    public int SelectedItemId { get; set; }
    public int SelectedEquipId { get; set; }
    public int EquipBoxId { get; set; }
    public int EquipBoxType { get; set; }
    public int WorkShopId { get; set; }
    public int WorkShopType { get; set; }
    public int WorkShopTotalCount { get; set; }
    public int WorkShopCurCount { get; set; }
    public int RefreshTimes { get; set; }
    public int FreeRefreshLimit { get; set; }
    public List<int> ItemIds { get; set; } = [];
    public List<int> EquipIds { get; set; } = [];
    public List<int> DestinyCharacterIds { get; set; } = [];
    public double RefreshDiscount { get; set; }
    public List<Theatre3NodeReward> FightRewards { get; set; } = [];
    public Theatre3NodeData? NodeData { get; set; }
    public Theatre3NodeData? ConnectNodeData { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3NodeData
{
    public int ChapterId { get; set; }
    public int NodeId { get; set; }
    public int Selected { get; set; }
    public List<Theatre3NodeSlot> Slots { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3NodeSlot
{
    public int SlotType { get; set; }
    public int SlotId { get; set; }
    public int Selected { get; set; }
    public int FightId { get; set; }
    public int FightTemplateId { get; set; }
    public int LinkGroupId { get; set; }
    public int EventId { get; set; }
    public int CurStepId { get; set; }
    public int ShopId { get; set; }
    public List<int> PassedStageIds { get; set; } = [];
    public List<int> PassedStepId { get; set; } = [];
    public List<Theatre3NodeReward> NodeRewards { get; set; } = [];
    public List<Theatre3ShopItem> ShopItems { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3NodeReward
{
    public int Uid { get; set; }
    public int RewardType { get; set; }
    public int ConfigId { get; set; }
    public int Count { get; set; }
    public int Received { get; set; }
    public int IsShow { get; set; }
    public int Tag { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3ShopItem
{
    public int ItemType { get; set; }
    public int Uid { get; set; }
    public int ItemId { get; set; }
    public int ItemBoxId { get; set; }
    public int EquipBoxId { get; set; }
    public int IsBuy { get; set; }
    public int IsLock { get; set; }
    public int Price { get; set; }
    public int DiscountPrice { get; set; }
    public double DiscountPercent { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3SettleData
{
    public int EndId { get; set; }
    public int NodeCount { get; set; }
    public int FightNodeCount { get; set; }
    public int ChapterCount { get; set; }
    public int TotalItemCount { get; set; }
    public int TotalEquipCount { get; set; }
    public int TotalSuitCount { get; set; }
    public int NodeCountScore { get; set; }
    public int FightNodeCountScore { get; set; }
    public int ChapterCountScore { get; set; }
    public int TotalItemCountScore { get; set; }
    public int TotalEquipCountScore { get; set; }
    public int TotalSuitCountScore { get; set; }
    public int TotalScore { get; set; }
    public int BPExp { get; set; }
    public int StrengthPoint { get; set; }
    public int DestinyValue { get; set; }
    public int QubitValueA { get; set; }
    public int QubitValueB { get; set; }
    public int FirstPassFlag { get; set; }
    public int FireItemCount { get; set; }
    public List<int> CurUnlockItemId { get; set; } = [];
    public List<int> CurUnlockEquipId { get; set; } = [];
    public List<int> UnlockItemId { get; set; } = [];
    public List<int> UnlockDifficultyId { get; set; } = [];
    public List<int> UnlockEquipId { get; set; } = [];
    public List<int> PassChapterIds { get; set; } = [];
    // Client initializes this as text and displays it with %s; match Bianca's text factor.
    // This is the documented local wire representation, not a captured retail primitive.
    public string EndFactor { get; set; } = string.Empty;
    public List<Theatre3Item> Items { get; set; } = [];
    public List<Theatre3Character> Characters { get; set; } = [];
    public List<Theatre3Equip> Equips { get; set; } = [];
    public List<Theatre3EquipPos> EquipPos { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, List<int>> PassDifficultyRecords { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3SelectDifficultyRequest
{
    public int Difficulty { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3SelectDifficultyResponse : Theatre3Response
{
    public int DifficultyId { get; set; }
    public int MaxEnergy { get; set; }
    public List<Theatre3EquipPos> EquipPos { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3SetTeamRequest
{
    public List<Theatre3EquipPosInfo> EquipPosInfos { get; set; } = [];
    public Theatre3TeamData TeamData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre3SetTeamResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3EndRecruitRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre3EndRecruitResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3SettleAdventureRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre3SettleAdventureResponse : Theatre3Response
{
    public Theatre3SettleData SettleData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre3SelectNodeRequest
{
    public int NodeId { get; set; }
    public int SlotId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3SelectNodeResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3SwitchParallelChapterRequest
{
    public int ChapterId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3SwitchParallelChapterResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3EventNodeNextStepRequest
{
    public int CurEventStepId { get; set; }
    public int OptionId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3EventNodeNextStepResponse : Theatre3Response
{
    public int NextEventStepId { get; set; }
    public int FightTemplateId { get; set; }
    public List<int> InnerItemIds { get; set; } = [];
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3EndNodeRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre3EndNodeResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3DestinySelectRequest
{
    public int CharacterId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3DestinySelectResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3SelectInitialItemRequest
{
    public int ItemId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3SelectInitialItemResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3SelectItemRewardRequest
{
    public int InnerItemId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3SelectItemRewardResponse : Theatre3Response
{
    public int InnerItemId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3NodeShopBuyItemRequest
{
    public int ShopItemUid { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3NodeShopBuyItemResponse : Theatre3Response
{
    public int InnerItemId { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> LotteryRewards { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3SelectEquipRequest
{
    public int SelectId { get; set; }
    public int Pos { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3SelectEquipResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3ChangeEquipPosRequest
{
    public int SrcSuitId { get; set; }
    public int SrcPos { get; set; }
    public int DstSuitId { get; set; }
    public int DstPos { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3ChangeEquipPosResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3RecastEquipRequest
{
    public int SrcEquipId { get; set; }
    public int DstSuitId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3RecastEquipResponse : Theatre3Response
{
    public int DstEquipId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3QubitEquipRequest
{
    public int EquipId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3QubitEquipResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3RefreshEquipBoxRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre3RefreshEquipBoxResponse : Theatre3Response
{
    public Theatre3Step Step { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre3EndEquipBoxRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre3EndEquipBoxResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3EndWorkShopRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre3EndWorkShopResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3LookEquipAttributeRequest
{
    public int EquipId { get; set; }
    public int SuitId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3LookEquipAttributeResponse : Theatre3Response
{
    public int CurBuyCount { get; set; }
    public int TotalRecvCoin { get; set; }
    public Theatre3EquipAttribute? Equip { get; set; }
    public Theatre3SuitAttribute? EquipSuit { get; set; }
    public Theatre3ItemAttribute? Item { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3RecvFightRewardRequest
{
    public int Uid { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3RecvFightRewardResponse : Theatre3Response
{
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3EndRecvFightRewardRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre3EndRecvFightRewardResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3ActivationStrengthenTreeRequest
{
    public int Id { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3ActivationStrengthenTreeResponse : Theatre3Response
{
}

[MessagePackObject(true)]
public sealed class Theatre3GetBattlePassRewardRequest
{
    public int GetRewardType { get; set; }
    public int Id { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3GetBattlePassRewardResponse : Theatre3Response
{
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
    public List<int> GetRewardIds { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3GetAchievementRewardRequest
{
    public int NeedCountId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3GetAchievementRewardResponse : Theatre3Response
{
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3ActivityData : Theatre3ActivityData
{
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3BattlePassExp
{
    public int TotalBattlePassExp { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3AddChapter
{
    public Theatre3ChapterDb Chapter { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3AddStep
{
    public int ChapterId { get; set; }
    public Theatre3Step Step { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3AddItem
{
    public List<Theatre3Item> InnerItems { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3Item
{
    public List<Theatre3Item> InnerItems { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3AdventureSettle
{
    public Theatre3SettleData SettleData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3NodeNextStep
{
    public int EventId { get; set; }
    public int NextStepId { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3AbnormalExit
{
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3EquipPosCapacityChange
{
    public List<Theatre3EquipPos> EquipPos { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3MaxEnergyChange
{
    public int MaxEnergy { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3EquipDatas
{
    public List<Theatre3Equip> Equips { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3QubitValues
{
    public int QubitValueA { get; set; }
    public int QubitValueB { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3DestinyValue
{
    public int DestinyValue { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3ChapterSwitch
{
    public bool ChapterSwitch { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3SwitchLine
{
    public int ChapterId { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre3PassFightNode
{
    public List<int> PassEventFightNodes { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3EquipAttribute
{
    public int EquipId { get; set; }
    public int PassFightCount { get; set; }
    public int PassBossFightCount { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3SuitAttribute
{
    public int SuitId { get; set; }
    public int PassFightCount { get; set; }
    public int PassBossFightCount { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3ItemAttribute
{
    public int ItemId { get; set; }
    public int PassFightCount { get; set; }
    public int PassBossFightCount { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre3FightRecord
{
    public int StageId { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre3DamageSource> DamageSourceDic { get; set; } = [];
    public Dictionary<string, List<int>> StringToListIntRecord { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre3DamageSource
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> DamageSource { get; set; } = [];
}

// Native XFightEventLevel, il2cpp dump.cs:67127-67132.
[MessagePackObject(true)]
public sealed class Theatre3FightEventLevel
{
    public int FightEventId { get; set; }
    public int FightEventLevel { get; set; }
}
