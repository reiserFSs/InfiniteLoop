using MessagePack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.MsgPack;

// EN matrix/xmodule/xtheatre4: XTheatre4Control request/response consumers and
// XTheatre4Agency notification registration; field names and collections are transcribed
// from xentity Notify* readers (the wire authority), never guessed.
// Requests: 27 in XTheatre4Control.RequestName plus the literal "Theatre4FightLocateRequest"
// sent by XTheatre4Agency.EnterFight.
// Collections follow the client readers: arrays where the entity loops with ipairs or
// re-keys by object id, Dictionary<int, ...> only where Lua indexes by a numeric key
// (TracebackDatas by day, FinishEventIds/GlobalFinishEventIds by event, ColorLevel etc.).

[MessagePackObject(true)]
public class Theatre4Response
{
    public int Code { get; set; }
}

//region data schemas

// EN xentity/XTheatre4Activity.lua Ctor + NotifyActivityData.
[MessagePackObject(true)]
public sealed class Theatre4ActivityData
{
    public int ActivityId { get; set; }
    public Theatre4AdventureData? AdventureData { get; set; }
    public Theatre4AdventureSettleData? PreAdventureSettleData { get; set; }
    public int TechPoint { get; set; }
    public List<int> Techs { get; set; } = [];
    public List<int> ItemsAtlas { get; set; } = [];
    public List<int> TalentAtlas { get; set; } = [];
    public List<int> MapAtlas { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Difficultys { get; set; } = [];
    public int TotalBattlePassExp { get; set; }
    public List<int> BattlePassGotRewardIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> PassAffixCounts { get; set; } = [];
    public List<int> PassMapBuildprints { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Endings { get; set; } = [];
    public int MaxPassChapterCount { get; set; }
    public int MaxScore { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> GlobalFinishEventIds { get; set; } = [];
}

// EN xentity/XTheatre4Adventure.lua Ctor + NotifyAdventureData.
[MessagePackObject(true)]
public sealed class Theatre4AdventureData
{
    public int Difficulty { get; set; }
    public int Affix { get; set; }
    public int InheritItemId { get; set; }
    public int Hp { get; set; }
    public int Ap { get; set; }
    public int MaxAp { get; set; }
    public int ExtraMaxAp { get; set; }
    public int Bp { get; set; }
    public int AwakeningPoint { get; set; }
    public int TracebackPoint { get; set; }
    public int Gold { get; set; }
    public int SettleBpExp { get; set; }
    public int Days { get; set; }
    public int Prosperity { get; set; }
    public int IdSequence { get; set; }
    public int ClashCount { get; set; }
    public int ItemLimit { get; set; }
    public List<int> ColorLimit { get; set; } = [];
    public List<int> RecruitTickets { get; set; } = [];
    public List<int> ItemBoxs { get; set; } = [];
    public List<Theatre4TransactionData> Transactions { get; set; } = [];
    public List<Theatre4EffectData> CustomEffects { get; set; } = [];
    public List<Theatre4ItemData> Items { get; set; } = [];
    public List<Theatre4ItemData> Props { get; set; } = [];
    public List<int> WaitItems { get; set; } = [];
    public List<Theatre4CharacterData> Characters { get; set; } = [];
    public Theatre4TeamData? TeamData { get; set; }
    public List<Theatre4ColorTalentData> Colors { get; set; } = [];
    public int MapBlueprintId { get; set; }
    public Theatre4FateData? Fate { get; set; }
    public List<Theatre4ChapterData> Chapters { get; set; } = [];
    public int ExploreCount { get; set; }
    public List<Theatre4PosData> DailyExplorePosSet { get; set; } = [];
    public List<int> DailyExploreColors { get; set; } = [];
    public Theatre4PosData? PreExplorePos { get; set; }
    public List<int> CreatedBoxGroupIds { get; set; } = [];
    public List<int> CreatedEventGroupIds { get; set; } = [];
    public List<int> CreatedShopGroupIds { get; set; } = [];
    public List<int> CreatedFightGroupIds { get; set; } = [];
    public List<int> FinishFightIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> FinishEventIds { get; set; } = [];
    public List<int> OptionIds { get; set; } = [];
    public bool EffectGridAlterBeforeBuilt { get; set; }
    public int EffectShopBuyTimes { get; set; }
    public int EffectSweepTimes { get; set; }
    public long StartTime { get; set; }
    // Indexed by day: Adventure:GetTracebackDataByDays(days) and the backtrack UI read tracbackDatas[minDay].
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre4TracebackData> TracebackDatas { get; set; } = [];
}

// EN xentity/XTheatre4ChapterData.lua Ctor + NotifyChapterData.
[MessagePackObject(true)]
public sealed class Theatre4ChapterData
{
    public int MapGroup { get; set; }
    public int MapId { get; set; }
    public List<Theatre4GridData> Grids { get; set; } = [];
    public int EliteCount { get; set; }
    public bool IsPass { get; set; }
    public int MaxTracebackDays { get; set; }
}

// EN xentity/XTheatre4Grid.lua Ctor + NotifyGridData. Length/Width stay client-local.
[MessagePackObject(true)]
public sealed class Theatre4GridData
{
    public int GridId { get; set; }
    public int Color { get; set; }
    public int ColorResource { get; set; }
    public int Type { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
    public int State { get; set; }
    public int ContentGroup { get; set; }
    public int ContentId { get; set; }
    public int DisabledDay { get; set; }
    public Theatre4FightData? Fight { get; set; }
    public Theatre4ShopData? Shop { get; set; }
    public Theatre4EventData? Event { get; set; }
    public Theatre4BuildingData? Building { get; set; }
}

// EN xentity/XTheatre4Fight.lua Ctor + NotifyFightData.
[MessagePackObject(true)]
public sealed class Theatre4FightData
{
    public int FightGroupId { get; set; }
    public int StageId { get; set; }
    public int HpPercent { get; set; }
    public int PunishCountdown { get; set; }
    public List<int> FightEvents { get; set; } = [];
    public List<Theatre4AssetData> Rewards { get; set; } = [];
}

// EN xentity/XTheatre4Shop.lua Ctor + NotifyShopData.
[MessagePackObject(true)]
public sealed class Theatre4ShopData
{
    public int ShopId { get; set; }
    public int RefreshTimes { get; set; }
    public List<Theatre4ShopGoodsData> Goods { get; set; } = [];
    public int FreeBuyTimes { get; set; }
    public int Discount { get; set; }
}

// EN xentity/XTheatre4ShopGoods.lua Ctor + NotifyShopGoodsData.
[MessagePackObject(true)]
public sealed class Theatre4ShopGoodsData
{
    public int GoodsId { get; set; }
    public int Stock { get; set; }
    public bool IsFree { get; set; }
}

// EN xentity/XTheatre4Event.lua Ctor + NotifyEventData.
[MessagePackObject(true)]
public sealed class Theatre4EventData
{
    public int EventId { get; set; }
    public int StageId { get; set; }
    public int StageScore { get; set; }
}

// EN xentity/XTheatre4Building.lua Ctor + NotifyBuildingData.
[MessagePackObject(true)]
public sealed class Theatre4BuildingData
{
    public int BuildingId { get; set; }
    public int BuildingType { get; set; }
}

// EN xentity/XTheatre4Transaction.lua Ctor + NotifyTransactionData.
[MessagePackObject(true)]
public sealed class Theatre4TransactionData
{
    public int Id { get; set; }
    public int Type { get; set; }
    public int ConfigId { get; set; }
    public List<int> Params { get; set; } = [];
    public List<Theatre4CharacterData> Characters { get; set; } = [];
    public List<Theatre4AssetData> Rewards { get; set; } = [];
    public List<int> SelectIds { get; set; } = [];
    public int SelectLimit { get; set; }
    public int SelectTimes { get; set; }
    public int RefreshTimes { get; set; }
    public int RefreshLimit { get; set; }
}

// EN xentity/XTheatre4Effect.lua Ctor + NotifyEffectData.
[MessagePackObject(true)]
public sealed class Theatre4EffectData
{
    public int Id { get; set; }
    public int EffectId { get; set; }
    public int Count { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> CustomData { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ColorResource { get; set; } = [];
    public int MarkupRate { get; set; }
    public int ItemUid { get; set; }
    public int Accumulate { get; set; }
    public int UseTimes { get; set; }
}

// EN xentity/XTheatre4Item.lua Ctor + NotifyItemData. LeftDays -1 is permanent.
[MessagePackObject(true)]
public sealed class Theatre4ItemData
{
    public int Uid { get; set; }
    public int ItemId { get; set; }
    public List<Theatre4EffectData> Effects { get; set; } = [];
    public int LeftDays { get; set; } = -1;
}

// EN xentity/XTheatre4CharacterData.lua Ctor + NotifyCharacterData.
[MessagePackObject(true)]
public sealed class Theatre4CharacterData
{
    public int CharacterId { get; set; }
    public int Star { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ColorLevelAdds { get; set; } = [];
}

// EN xentity/XTheatre4TeamData.lua Ctor + NotifyTeamData.
[MessagePackObject(true)]
public sealed class Theatre4TeamData
{
    public int CaptainPos { get; set; }
    public int FirstFightPos { get; set; }
    public int EnterCgIndex { get; set; }
    public int SettleCgIndex { get; set; }
    // XTheatre4Team.GetTeamData() sends it; the notify-back entity ignores it.
    public int GeneralSkill { get; set; }
    public List<int> CardIds { get; set; } = [];
    public List<int> RobotIds { get; set; } = [];
}

// EN xentity/XTheatre4ColorTalent.lua Ctor + NotifyColorData.
[MessagePackObject(true)]
public sealed class Theatre4ColorTalentData
{
    public int Color { get; set; }
    public int Level { get; set; } = 1;
    public int Resource { get; set; }
    public int PointCanCost { get; set; }
    public int DailyResource { get; set; }
    public int Point { get; set; }
    public List<Theatre4ColorTalentSlotData> Slots { get; set; } = [];
    public Theatre4ColorTalentWaitSlotData? WaitSlot { get; set; }
}

// EN xentity/XTheatre4ColorTalentSlot.lua Ctor + NotifySlotData.
[MessagePackObject(true)]
public sealed class Theatre4ColorTalentSlotData
{
    public int SlotId { get; set; }
    public List<Theatre4TalentData> Talents { get; set; } = [];
}

// EN xentity/XTheatre4Talent.lua Ctor + NotifyTalentData.
[MessagePackObject(true)]
public sealed class Theatre4TalentData
{
    public int TalentId { get; set; }
    public List<Theatre4EffectData> Effects { get; set; } = [];
}

// EN xentity/XTheatre4ColorTalentWaitSlot.lua Ctor + NotifyWaitSlotData.
[MessagePackObject(true)]
public sealed class Theatre4ColorTalentWaitSlotData
{
    public int SlotId { get; set; }
    public List<int> TalentIds { get; set; } = [];
    public int RefreshFreeTimes { get; set; }
    public int RefreshLimit { get; set; }
    public int RefreshTimes { get; set; }
}

// EN xentity/XTheatre4Adventure.lua Fate container; reused by NotifyTheatre4FateData.
[MessagePackObject(true)]
public sealed class Theatre4FateData
{
    public int Id { get; set; }
    public List<Theatre4FateEventData> FateEvents { get; set; } = [];
}

// EN xentity/XTheatre4Fate.lua NotifyFateData: wire keys are TableRowId/UniqueId.
[MessagePackObject(true)]
public sealed class Theatre4FateEventData
{
    public int TableRowId { get; set; }
    public int UniqueId { get; set; }
    public Theatre4EventData? Event { get; set; }
    public int EventTimeLeft { get; set; }
}

// EN xentity/XTheatre4Pos.lua Ctor + NotifyPosData.
[MessagePackObject(true)]
public sealed class Theatre4PosData
{
    public int MapId { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
}

// EN xentity/XTheatre4Asset.lua Ctor + NotifyAssetData.
[MessagePackObject(true)]
public sealed class Theatre4AssetData
{
    public int Type { get; set; }
    public int Id { get; set; }
    public int Num { get; set; }
    public int RewardId { get; set; }
}

// EN xentity/XTheatre4AdventureSettle.lua Ctor + NotifyPreAdventureSettleData.
[MessagePackObject(true)]
public sealed class Theatre4AdventureSettleData
{
    public int SettleType { get; set; }
    public int EndingId { get; set; }
    public int GridPassCount { get; set; }
    public List<int> InheritItems { get; set; } = [];
    public int Prosperity { get; set; }
    public int StarBpExp { get; set; }
    public List<RewardGoods> RewardGoods { get; set; } = [];
    public long SettleTime { get; set; }
}

// EN xentity/XTheatre4DailySettleResult.lua Ctor + NotifyDailySettleResult (wire fields only).
[MessagePackObject(true)]
public sealed class Theatre4DailySettleResultData
{
    public int Interest { get; set; }
    public int InterestLimit { get; set; }
    public int BuildPoint { get; set; }
    public Dictionary<int, int> ColorLevel { get; set; } = [];
    public Dictionary<int, int> ColorResource { get; set; } = [];
    public double ColorExtra { get; set; }
}

// EN xui/xuitheatre4/game/bubble/XUiTheatre4BubbleBacktrack.lua reads Days/CostAp/CostGold/CostBp/Colors.
[MessagePackObject(true)]
public sealed class Theatre4TracebackData
{
    public int Days { get; set; }
    public int CostAp { get; set; }
    public int CostGold { get; set; }
    public int CostBp { get; set; }
    public List<Theatre4TracebackColorData> Colors { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre4TracebackColorData
{
    public int Color { get; set; }
    public int Level { get; set; }
    public int Resource { get; set; }
}

//endregion

//region requests and responses

[MessagePackObject(true)]
public sealed class Theatre4EnterRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre4EnterResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4SelectDifficultRequest
{
    public int Difficult { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4SelectDifficultResponse : Theatre4Response
{
    public Theatre4AdventureData? AdventureData { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4SelectInheritRequest
{
    public int ItemId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4SelectInheritResponse : Theatre4Response
{
    public Theatre4AdventureData? AdventureData { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4SelectAffixRequest
{
    public int Affix { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4SelectAffixResponse : Theatre4Response
{
    public Theatre4AdventureData? AdventureData { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ExploreGridRequest
{
    public int MapId { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ExploreGridResponse : Theatre4Response
{
    public Theatre4GridData? Grid { get; set; }
    public List<Theatre4GridData> OtherGrids { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre4BattlePassGetRewardRequest
{
    public int GetRewardType { get; set; }
    public int Id { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4BattlePassGetRewardResponse : Theatre4Response
{
    public List<int> GotRewardIds { get; set; } = [];
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre4TechUnlockRequest
{
    public int TechId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4TechUnlockResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4RefreshRecruitRequest
{
    public int TransactionId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4RefreshRecruitResponse : Theatre4Response
{
    public Theatre4TransactionData? Transaction { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ConfirmRecruitRequest
{
    public int TransactionId { get; set; }
    public int Index { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ConfirmRecruitResponse : Theatre4Response
{
    public Theatre4TransactionData? Transaction { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4SettleAdventureRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre4SettleAdventureResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4DailySettleRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre4DailySettleResponse : Theatre4Response
{
    public Theatre4DailySettleResultData? SettleResult { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ShopBuyRequest
{
    public int MapId { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
    public int GoodsIndex { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ShopBuyResponse : Theatre4Response
{
    public Theatre4ShopData? Shop { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4RefreshGoodsRequest
{
    public int MapId { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4RefreshGoodsResponse : Theatre4Response
{
    public Theatre4ShopData? Shop { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ConfirmDropRequest
{
    public int TransactionId { get; set; }
    public int Index { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ConfirmDropResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4ConfirmFightRewardRequest
{
    public int TransactionId { get; set; }
    public int Index { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ConfirmFightRewardResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4QuitFightRewardRequest
{
    public int TransactionId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4QuitFightRewardResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4ConfirmItemRequest
{
    public int TransactionId { get; set; }
    public int OperateType { get; set; }
    public int Index { get; set; }
    public int ReplaceItemUid { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ConfirmItemResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4DoGridEventRequest
{
    public int MapId { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
    public int Option { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4DoGridEventResponse : Theatre4Response
{
    public List<Theatre4GridData> Grids { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre4SetTeamDataRequest
{
    public Theatre4TeamData TeamData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre4SetTeamDataResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4DoFateEventRequest
{
    public int Option { get; set; }
    public int FateEventUniqueId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4DoFateEventResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4SelectTalentRequest
{
    public int Color { get; set; }
    public int TalentId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4SelectTalentResponse : Theatre4Response
{
    public Theatre4ColorTalentData? ColorTalent { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4RefreshTalentRequest
{
    public int Color { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4RefreshTalentResponse : Theatre4Response
{
    public Theatre4ColorTalentData? ColorTalent { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ReplaceItemRequest
{
    public int WaitItemId { get; set; }
    public int TargetItemUid { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ReplaceItemResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4UseSkillEffectRequest
{
    public int EffectId { get; set; }
    public List<int> Params { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre4UseSkillEffectResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4SweepMosnterRequest
{
    public int MapId { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
    public int SweepType { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4SweepMosnterResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4ItemRecyclingRequest
{
    public int Uid { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4ItemRecyclingResponse : Theatre4Response
{
}

[MessagePackObject(true)]
public sealed class Theatre4WaitItemRecyclingRequest
{
    public int ItemId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4WaitItemRecyclingResponse : Theatre4Response
{
}

// Sent as the literal "Theatre4FightLocateRequest" by XTheatre4Agency.EnterFight.
[MessagePackObject(true)]
public sealed class Theatre4FightLocateRequest
{
    public int Type { get; set; }
    public int MapId { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre4FightLocateResponse : Theatre4Response
{
}

//endregion

//region notifications

// EN XTheatre4Agency notification registration and the fields each handler reads.
[MessagePackObject(true)]
public sealed class NotifyTheatre4ActivityData
{
    public Theatre4ActivityData Data { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4AdventureData
{
    public Theatre4AdventureData? AdventureData { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4AddChapter
{
    public Theatre4ChapterData Chapter { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4Transactions
{
    public List<Theatre4TransactionData> Transactions { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4FateData
{
    public Theatre4FateData? FateData { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4ColorResourceData
{
    public int Color { get; set; }
    public int Resource { get; set; }
    public int Level { get; set; }
    public int DailyResource { get; set; }
    public int Point { get; set; }
    public int PointCanCost { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4ColorTalentAddInfo
{
    public List<int> TalentIds { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4BattlePassExp
{
    public int TotalBattlePassExp { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4ColorTalentData
{
    public List<Theatre4ColorTalentData> ColorTalents { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4RecruitTicks
{
    public List<int> RecruitTicks { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4ItemBoxs
{
    public List<int> ItemBoxs { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4ChangeGrids
{
    public int MapId { get; set; }
    public List<Theatre4GridData> Grids { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4AdventureSettle
{
    public Theatre4AdventureSettleData? SettleData { get; set; }
    public Theatre4AdventureData? AdventureData { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4Reward
{
    public List<Theatre4AssetData> Rewards { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4RemoveItem
{
    public Theatre4ItemData? Item { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4RemoveTransaction
{
    public int TrxId { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4AddTransaction
{
    public Theatre4TransactionData? Trx { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4CustomEffects
{
    public List<Theatre4EffectData> Effects { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4CustomResource
{
    public int Gold { get; set; }
    public int Hp { get; set; }
    public int Ap { get; set; }
    public int Bp { get; set; }
    public int AwakeningPoint { get; set; }
    public int TracebackPoint { get; set; }
    public int Prosperity { get; set; }
    public int ItemLimit { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4CustomCounter
{
    public int EffectShopBuyTimes { get; set; }
    public int EffectSweepTimes { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4EffectsChange
{
    public List<Theatre4EffectData> Effects { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4ItemAdd
{
    public Theatre4ItemData? Item { get; set; }
    public int WaitItem { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4ItemUpdate
{
    public Theatre4ItemData? Item { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4Atlas
{
    public List<int> ItemsAtlas { get; set; } = [];
    public List<int> TalentAtlas { get; set; } = [];
    public List<int> MapAtlas { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4ExtraMaxAp
{
    public int ExtraMaxAp { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4CharacterUpdate
{
    public Theatre4CharacterData? Character { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4FinishFightRecord
{
    public List<int> FinishFightIds { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4FinishEventRecord
{
    public Dictionary<int, int> FinishEventIds { get; set; } = [];
    public Dictionary<int, int> GlobalFinishEventIds { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4AbnormalExit
{
    public int ClashCount { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4FinishDifficultys
{
    public Dictionary<int, int> Difficultys { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4FinishEndings
{
    public Dictionary<int, int> Endings { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4TracebackInfo
{
    public Dictionary<int, Theatre4TracebackData> TracebackDatas { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre4SingleTracebackData
{
    public Theatre4TracebackData? Data { get; set; }
}

//endregion
