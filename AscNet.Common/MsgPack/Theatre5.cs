using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;

namespace AscNet.Common.MsgPack;

// Lua contract: EN matrix/xmodule/xtheatre5 agencies and entity consumers.
// The native dump contains no full Theatre5 DB, shop, mission, effect or RPC declarations
// except DlcSingleFightSettleRequest. Their integral fields use int as a local model,
// not a proven retail width; response packet names follow Request -> Response.
// Native closure below is transcribed from il2cppdumper-runtime-metadata/dump.cs:
// XWorldData, XDlcFightResultData, XAutoChessGameplayResult and DlcReportWorldResult.
// Native enum fields retain their declared Int32 wire storage. Reference fields may be nil.
// Collections use MessagePack arrays/maps; native Stack RunEvents is kept in wire order
// as a List (newest/current event first), not reconstructed by reversing stack pushes.

[MessagePackObject(true)]
public class Theatre5Response
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5DataDb
{
    public int ActivityId { get; set; }
    public int PvpType { get; set; }
    public Theatre5PvpAdventureData? PvpAdventureData { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5PvpCharacter> Characters { get; set; } = [];
    public Theatre5PveAdventureData? PveAdventureData { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5PveCharacter> PveCharacters { get; set; } = [];
    public List<int> PvpChooseMissionBounty { get; set; } = [];
    public List<int> PveChooseMissionBounty { get; set; } = [];
    public int CurPveStoryLineId { get; set; }
    public int CurStoryEntranceId { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5StoryLine> PveStoryLines { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Clue> PveClues { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Script> PveScripts { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5HistoryChapter> HistoryChapters { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> CommonFightCnt { get; set; } = [];
    public List<int> RelicCollects { get; set; } = [];
}

[MessagePackObject(true)]
public class Theatre5AdventureData
{
    public int Version { get; set; }
    public int CharacterId { get; set; }
    public int Status { get; set; }
    public int RoundNum { get; set; }
    public int GoldNum { get; set; }
    public int Health { get; set; }
    public int IdSequence { get; set; }
    public int CheckFailTimes { get; set; }
    public Theatre5ShopData? ShopData { get; set; }
    public Theatre5BagData BagData { get; set; } = new();
    public Theatre5SkillChoiceData? SkillChoiceData { get; set; }
    public int CharacterLv { get; set; }
    public int CharacterExp { get; set; }
    public List<Theatre5Item> RandomRelics { get; set; } = [];
    public int UseRelicRefreshCount { get; set; }
    public bool IsCanFreeUnlockGrid { get; set; }
    public int EnterShopCnt { get; set; }
    public int LeaveShopCnt { get; set; }
    public int EffectFreeRefreshCnt { get; set; }
    public int RuneAutoStrengthenCnt { get; set; }
    public List<int> RelicOrders { get; set; } = [];
    public List<Theatre5Effect> EffectQueue { get; set; } = [];
    public Theatre5Mission? Missioning { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Mission> ChooseMissions { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> FreshMissionCounts { get; set; } = [];
    public List<int> FreshMissionBounty { get; set; } = [];
    public List<int> FreshMissionCondition { get; set; } = [];
    public int MissionLevelUpForRound { get; set; }
    // Server-owned mission evidence persists with each adventure, never on the wire.
    [IgnoreMember, BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> MissionBattleConditions { get; set; } = [];
    [IgnoreMember, BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> MissionBattleAttributes { get; set; } = [];
    [IgnoreMember]
    public int MissionBattleCountedRound { get; set; } = -1;
    [IgnoreMember]
    public int MissionChoiceRound { get; set; } = -1;
    [IgnoreMember]
    public HashSet<string> TriggeredRelicEffects { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5PvpAdventureData : Theatre5AdventureData
{
    public int TrophyNum { get; set; }
    public Theatre5MatchEnemy? EnemyData { get; set; }
    public bool IsPvpExtra { get; set; }
    public int NormalOriginRating { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5PveAdventureData : Theatre5AdventureData
{
    public Theatre5PveChapterData? PveChapterData { get; set; }
    public List<Theatre5ItemBoxSelection> ItemBoxSelectData { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5PvpCharacter
{
    public int Id { get; set; }
    public int Rating { get; set; }
    public bool IsUnlockAnimation { get; set; }
    public int RankProtectNum { get; set; }
    public List<int> RewardRanks { get; set; } = [];
    public int FashionId { get; set; }
}

// PvE character dictionary membership supplies the ID; Lua only reads FashionId.
[MessagePackObject(true)]
public sealed class Theatre5PveCharacter
{
    public int FashionId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5StoryLine
{
    public int StoryLineId { get; set; }
    public int CurContentId { get; set; }
    public List<int> FinishContents { get; set; } = [];
    public int PveCharacterId { get; set; }
    public Theatre5PveChapterData? PveChapterData { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5Clue
{
    public int ClueId { get; set; }
    public bool IsComplete { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5Script
{
    public int ScriptId { get; set; }
    public int CurStep { get; set; }
    public bool IsComplete { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5HistoryChapter
{
    public int ChapterId { get; set; }
    public bool IsEnterAvgPlay { get; set; }
    public bool IsPassAvgPlay { get; set; }
    public List<int> FinishEvents { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5Item
{
    public int InstanceId { get; set; }
    public int ItemId { get; set; }
    public int ItemType { get; set; }
    public bool IsStrengthen { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5BagData
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Item> BagItemDict { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Item> TempItemDict { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Item> SkillDict { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Item> RuneDict { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Item> RelicDict { get; set; } = [];
    public int BagGridsNum { get; set; }
    public int SkillGridsNum { get; set; }
    public int RuneGridsNum { get; set; }
    public int RoundNumWithoutGridUnlock { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5ShopData
{
    public int ShopId { get; set; }
    public List<Theatre5Goods> Goods { get; set; } = [];
    public int UnlockGridsNum { get; set; }
    public int RefreshCnt { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5Goods
{
    public Theatre5Item ItemInfo { get; set; } = new();
    public bool IsSpecialPrice { get; set; }
    public bool IsSoldOut { get; set; }
    public bool IsFreeze { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5SkillChoiceData
{
    [BsonSerializer(typeof(Theatre5SkillChoiceItemListSerializer))]
    public List<Theatre5Item> SkillGroups { get; set; } = [];
}

// The old server persisted skill choices as shop goods. Read that one shape so
// PrepareLogin can rewrite active runs as the same flat items sent to the client.
public sealed class Theatre5SkillChoiceItemListSerializer : SerializerBase<List<Theatre5Item>>
{
    public override List<Theatre5Item> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        List<Theatre5Item> items = [];
        context.Reader.ReadStartArray();
        while (context.Reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            BsonDocument value = BsonDocumentSerializer.Instance.Deserialize(context, args);
            if (value.TryGetValue(nameof(Theatre5Goods.ItemInfo), out BsonValue? wrapped))
                value = wrapped.AsBsonDocument;
            items.Add(BsonSerializer.Deserialize<Theatre5Item>(value));
        }
        context.Reader.ReadEndArray();
        return items;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, List<Theatre5Item> value)
    {
        context.Writer.WriteStartArray();
        foreach (Theatre5Item item in value)
            BsonSerializer.Serialize(context.Writer, item);
        context.Writer.WriteEndArray();
    }
}

[MessagePackObject(true)]
public sealed class Theatre5Mission
{
    public int MissionId { get; set; }
    public Theatre5MissionBounty MissionBounty { get; set; } = new();
    public Theatre5MissionCondition MissionCondition { get; set; } = new();
    public int MissionState { get; set; }
    public int MissionRelicId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5MissionBounty
{
    public int Bounty { get; set; }
    public int BountyLevel { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5MissionCondition
{
    public int ConditionId { get; set; }
    public int ConditionCounter { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5ItemBoxSelection
{
    public int BoxInstanceId { get; set; }
    public List<Theatre5Item> ItemList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5MatchEnemy
{
    public string Name { get; set; } = string.Empty;
    public int HeadPortraitId { get; set; }
    public int HeadFrameId { get; set; }
    public int IsUseSkin { get; set; }
    public int CharacterId { get; set; }
    public List<int> SkillIds { get; set; } = [];
    public List<Theatre5RuneEvolve> RuneEvolves { get; set; } = [];
    public int MissionId { get; set; }
    public int MissionBountyLevel { get; set; }
    public int MissionRelicId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5RankPlayer
{
    public int Id { get; set; }
    public int HeadPortraitId { get; set; }
    public int HeadFrameId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public int Theatre5RankCharacterId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5EventReward
{
    public List<Theatre5Item> Items { get; set; } = [];
    public int GoldNum { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5BagUpdate
{
    public int UpdateType { get; set; }
    public bool IsTempBag { get; set; }
    public int Index { get; set; }
    public Theatre5Item Item { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre5AddBuffResult
{
    public List<int> Buffs { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5RandomItemGroupEffectResult
{
    public List<Theatre5BagUpdate> UpdateItems { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5AddItemGroupEffectResult
{
    public Theatre5BagUpdate UpdateItem { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre5ChangeGoldResult
{
    public int NewGold { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5AddFreeShopFreshCntResult
{
    public int NewEffectFreeRefreshCnt { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5AddExpResult
{
    public int NewExp { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5RandomStealRuneResult
{
    public List<Theatre5BagUpdate> UpdateItems { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5AddAutoStrengthenCntResult
{
    public int NewAutoStrengthenCnt { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5AddAttrResult
{
    public int AttrType { get; set; }
    public int FixVal { get; set; }
    public int RateVal { get; set; }
    public int SpecificVal { get; set; }
    public int SpecificRateVal { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5AutoSellRuneResult
{
    public Theatre5BagUpdate UpdateItem { get; set; } = new();
    public int SellGold { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5AutoRuneReplaceResult
{
    public List<Theatre5BagUpdate> UpdateItems { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5ChangeHpEffectResult
{
    public int NewHp { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5ReplaceShopGoodsResult
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Goods> ReplaceShopGoods { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5Effect
{
    public int Type { get; set; }
    public Theatre5AddBuffResult? AddBuffResult { get; set; }
    public Theatre5RandomItemGroupEffectResult? RandomItemGroupEffectResult { get; set; }
    public Theatre5AddItemGroupEffectResult? AddItemGroupEffectResult { get; set; }
    public Theatre5ChangeGoldResult? ChangeGoldResult { get; set; }
    public Theatre5AddFreeShopFreshCntResult? AddFreeShopFreshCntResult { get; set; }
    public Theatre5AddExpResult? AddExpResult { get; set; }
    public Theatre5RandomStealRuneResult? RandomStealRuneResult { get; set; }
    public Theatre5AddAutoStrengthenCntResult? AddAutoStrengthenCntResult { get; set; }
    public Theatre5AddAttrResult? AddAttrResult { get; set; }
    public Theatre5AutoSellRuneResult? AutoSellRuneResult { get; set; }
    public Theatre5AutoRuneReplaceResult? AutoRuneReplaceResult { get; set; }
    public Theatre5ChangeHpEffectResult? ChangeHpEffectResult { get; set; }
    public Theatre5ReplaceShopGoodsResult? ReplaceShopGoodsResult { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5SkillChoiceRequest
{
    public int InstanceId { get; set; }
    public bool IsEquipped { get; set; }
    public int TargetIndex { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5SkillChoiceResponse : Theatre5Response
{
    public int Status { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Mission> ChooseMissions { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopUnlockGridRequest
{
    public int GridType { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopUnlockGridResponse : Theatre5Response
{
    public Theatre5BagData? BagData { get; set; }
    public int GoldNum { get; set; }
    public bool IsCanFreeUnlockGrid { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5EnterShopRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre5EnterShopResponse : Theatre5Response
{
    public int EnterShopCnt { get; set; }
    public int Status { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5Mission> ChooseMissions { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopBuyItemRequest
{
    public int InstanceId { get; set; }
    public bool IsEquipped { get; set; }
    public int TargetIndex { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopBuyItemResponse : Theatre5Response
{
    public int GoldNum { get; set; }
    public Theatre5BagData? BagData { get; set; }
    public Theatre5ShopData? ShopData { get; set; }
    public int RuneAutoStrengthenCnt { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopSellItemRequest
{
    public int InstanceId { get; set; }
    public bool IsEquipped { get; set; }
    public int ItemType { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopSellItemResponse : Theatre5Response
{
    public int GoldNum { get; set; }
    public Theatre5BagData? BagData { get; set; }
    public Theatre5ShopData? ShopData { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopRefreshRequest
{
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopRefreshResponse : Theatre5Response
{
    public Theatre5ShopData? ShopData { get; set; }
    public int GoldNum { get; set; }
    public int UpdateEffectFreeRefreshCnt { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5BagItemMoveRequest
{
    public int InstanceId { get; set; }
    public int ItemType { get; set; }
    public bool SrcEquipped { get; set; }
    public int SrcIndex { get; set; }
    public bool SrcIsTempItem { get; set; }
    public bool TargetEquipped { get; set; }
    public int TargetIndex { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5BagItemMoveResponse : Theatre5Response
{
    public Theatre5BagData? BagData { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopFreezeRequest
{
    public int InstanceId { get; set; }
    public bool IsFreeze { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ShopFreezeResponse : Theatre5Response
{
    public Theatre5ShopData? ShopData { get; set; }
}

[MessagePackObject(true)]
public sealed class PveOrPvpChangeRequest
{
}

[MessagePackObject(true)]
public sealed class PveOrPvpChangeResponse : Theatre5Response
{
}

[MessagePackObject(true)]
public sealed class DlcSingleEnterFightRequest
{
    public int WorldId { get; set; }
    public int? LevelId { get; set; }
}

[MessagePackObject(true)]
public sealed class DlcSingleEnterFightResponse : Theatre5Response
{
    public Theatre5WorldData? WorldData { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5CharacterSkinSetRequest
{
    public int CharacterId { get; set; }
    public int FashionId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5CharacterSkinSetResponse : Theatre5Response
{
}

[MessagePackObject(true)]
public sealed class Theatre5MissionFreshRequest
{
    public int PositionId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5MissionFreshResponse : Theatre5Response
{
    public int FreshMissionCnt { get; set; }
    public Theatre5Mission? FreshMission { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5MissionChooseRequest
{
    public int PositionId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5MissionChooseResponse : Theatre5Response
{
    public Theatre5Mission? ChooseMission { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5MissionLevelUpRequest
{
    public int CurLevel { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5MissionLevelUpResponse : Theatre5Response
{
    public int CurLevel { get; set; }
    public int CostGoldNum { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5MissionRewardRequest
{
    public int ChooseItemId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5MissionRewardResponse : Theatre5Response
{
    public int MissionState { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5CharacterLevelUpRequest
{
}

[MessagePackObject(true)]
public sealed class XTheatre5CharacterLevelUpResponse : Theatre5Response
{
    public int CharacterLv { get; set; }
    public int CharacterExp { get; set; }
    public int Status { get; set; }
    public int UseRefreshCount { get; set; }
    public List<Theatre5Item> RandomRelics { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class XTheatre5RelicRefreshRequest
{
}

[MessagePackObject(true)]
public sealed class XTheatre5RelicRefreshResponse : Theatre5Response
{
    public int UseRefreshCount { get; set; }
    public List<Theatre5Item> RandomRelics { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class XTheatre5RelicChooseRequest
{
    public int InstanceId { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5RelicChooseResponse : Theatre5Response
{
    public int Status { get; set; }
    public int UseRefreshCount { get; set; }
    public List<Theatre5Item> RandomRelics { get; set; } = [];
    public Theatre5Item? ChooseRelic { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5HammerStrengthenRequest
{
    public int HammerId { get; set; }
    public int RuneId { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5HammerStrengthenResponse : Theatre5Response
{
    public int HammerId { get; set; }
    public Theatre5Item? Rune { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5BuyExpRequest
{
    public int Exp { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5BuyExpResponse : Theatre5Response
{
    public int BuyExp { get; set; }
    public int CostGold { get; set; }
}

[MessagePackObject(true)]
public sealed class PveStoryLinePromoteRequest
{
    public int StoryLineId { get; set; }
    public int ContentId { get; set; }
    public int? SelectId { get; set; }
}

// The wire key says adventure; RougeData stores it as the story chapter data.
[MessagePackObject(true)]
public sealed class PveStoryLinePromoteResponse : Theatre5Response
{
    public int CurContentId { get; set; }
    public Theatre5PveChapterData? PveAdventureData { get; set; }
}

[MessagePackObject(true)]
public sealed class PveEventPromoteRequest
{
    public int EventId { get; set; }
    public int? OptionId { get; set; }
}

[MessagePackObject(true)]
public sealed class PveEventPromoteResponse : Theatre5Response
{
    public int ClueId { get; set; }
    public int NextEventId { get; set; }
    public int Exp { get; set; }
    public Theatre5EventReward? PveEventReward { get; set; }
    public Theatre5EventReward? ExtPveEventReward { get; set; }
}

[MessagePackObject(true)]
public sealed class PveChapterEnterRequest
{
    public int? StoryEntranceId { get; set; }
    public int StoryLineId { get; set; }
    public int CharacterId { get; set; }
}

[MessagePackObject(true)]
public sealed class PveChapterEnterResponse : Theatre5Response
{
    public Theatre5PveAdventureData? PveAdventureData { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ItemBoxSelectRequest
{
    public int BoxInstanceId { get; set; }
    public int ItemInstanceId { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ItemBoxSelectResponse : Theatre5Response
{
}

[MessagePackObject(true)]
public sealed class XTheatre5ItemBoxOpenRequest
{
    public int BoxInstanceId { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5ItemBoxOpenResponse : Theatre5Response
{
    public int OpenType { get; set; }
    public int UsedInstanceId { get; set; }
    public List<Theatre5Item> ItemBoxSelectData { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class PveAvgPlayRequest
{
    public int ChapterId { get; set; }
    public bool IsEnterAvg { get; set; }
}

[MessagePackObject(true)]
public sealed class PveAvgPlayResponse : Theatre5Response
{
}

[MessagePackObject(true)]
public sealed class XTheatre5PveAnswerQuestionRequest
{
    public int ScriptId { get; set; }
    public int Step { get; set; }
    public int IsCorrect { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5PveAnswerQuestionResponse : Theatre5Response
{
    public bool IsScriptCompleted { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5InitGameRequest
{
    public int CharacterId { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5InitGameResponse : Theatre5Response
{
    public Theatre5PvpAdventureData? PvpAdventureData { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5QueryRankRequest
{
    public int CharacterId { get; set; }
}

[MessagePackObject(true)]
public sealed class XTheatre5QueryRankResponse : Theatre5Response
{
    public int SelfRank { get; set; }
    public int TotalCount { get; set; }
    public List<Theatre5RankPlayer> RankPlayerInfos { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class DlcSingleFightSettleRequest
{
    public Theatre5DlcReportWorldResult DlcReportWorldResult { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class DlcSingleFightSettleResponse : Theatre5Response
{
    public Theatre5DlcFightSettleData? DlcFightSettleData { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5SettleExtraChoiceRequest
{
    public bool IsExtra { get; set; }
}

[MessagePackObject(true)]
public sealed class Theatre5SettleExtraChoiceResponse : Theatre5Response
{
    public Theatre5AutoChessGameplayResult? SettleResult { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class Theatre5MatchRequest
{
}

[MessagePackObject(true)]
public sealed class Theatre5MatchResponse : Theatre5Response
{
    public Theatre5MatchEnemy? EnemyData { get; set; }
    public int LeaveShopCnt { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre5ActivityData
{
    public Theatre5DataDb Theatre5DataDb { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre5UnlockCharacter
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5PvpCharacter> PvpCharacters { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5PveCharacter> PveCharacters { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre5AdventureData
{
    public Theatre5PvpAdventureData? PvpAdventureData { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatre5ShopUpdate
{
    public int GoldNum { get; set; }
    public Theatre5ShopData ShopData { get; set; } = new();
    public Theatre5BagData BagData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre5SkillChoiceUpdate
{
    public int GoldNum { get; set; }
    public Theatre5BagData BagData { get; set; } = new();
    public Theatre5SkillChoiceData? SkillChoiceData { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyPveStoryLineUnlock
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5StoryLine> PveStoryLines { get; set; } = [];
}

// Registered by Lua but its consumer is empty and no native declaration exists.
// Payload authority is missing; do not use this notification to synchronize inventory.
[MessagePackObject(true)]
public sealed class NotifyTheatre5AddItem
{
}

[MessagePackObject(true)]
public sealed class NotifyTheatre5BagDataUpdate
{
    public int Status { get; set; }
    public int GoldNum { get; set; }
    public Theatre5BagData BagData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatre5Effect
{
    public List<Theatre5Effect> EffectQueue { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatre5Mission
{
    public Theatre5Mission Mission { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre5DlcFightSettleData
{
    public Theatre5DlcFightResultData ResultData { get; set; } = new();
    public Theatre5AutoChessGameplayResult? XAutoChessGameplayResult { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

// Native XWorldData.
[MessagePackObject(true)]
public sealed class Theatre5WorldData
{
    public bool Online { get; set; }
    public string RoomId { get; set; } = string.Empty;
    public int WorldId { get; set; }
    public int LevelId { get; set; }
    public int MissionId { get; set; }
    public List<Theatre5WorldPlayerData> Players { get; set; } = [];
    public int RebootId { get; set; }
    public bool IsTeaching { get; set; }
    public bool IsLocalDebug { get; set; }
    public int WorldType { get; set; }
    public Theatre5WorldSaveData? WorldSaveData { get; set; }
    public int ServerControllerSeed { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> PlayerSeeds { get; set; } = [];
    public bool OpenRpcMerge { get; set; }
    public Theatre5DlcQuestInfo? QuestData { get; set; }
    public Theatre5AutoChessGameplayData? AutoChessGameplayData { get; set; }
    public Theatre5Theatre6GameplayData? Theatre6GameplayData { get; set; }
    public bool IsSingleOnline { get; set; }
}

// Native XWorldPlayerData.
[MessagePackObject(true)]
public sealed class Theatre5WorldPlayerData
{
    public int Id { get; set; }
    public bool Master { get; set; }
    public int CurNpcPos { get; set; }
    public bool IsLeader { get; set; }
    public string Name { get; set; } = string.Empty;
    public int HeadPortraitId { get; set; }
    public int HeadFrameId { get; set; }
    public List<Theatre5WorldNpcData> NpcList { get; set; } = [];
    public int RebootCount { get; set; }
    public Theatre5WorldPlayerBornData? BornData { get; set; }
    public Theatre5DlcMultiplayerPlayerData? MultiplayerData { get; set; }
    public Theatre5DlcRelinkPlayerData? RelinkPlayerData { get; set; }
}

// Native XWorldNpcData.
[MessagePackObject(true)]
public sealed class Theatre5WorldNpcData
{
    public int Id { get; set; }
    public int Level { get; set; }
    public int Pos { get; set; }
    public int TrialId { get; set; }
    public bool IsPlayerSelf { get; set; }
    public int Gender { get; set; }
    public Theatre5DlcCharacterData? Character { get; set; }
    public List<Theatre5DlcChipData> Chips { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> MagicId2Level { get; set; } = [];
    public Theatre5DlcChipData? SelfAssistChipData { get; set; }
    public Theatre5WorldNpcPartData? PartData { get; set; }
    public byte[] AttribsData { get; set; } = [];
}

// Native XDlcCharacterData.
[MessagePackObject(true)]
public sealed class Theatre5DlcCharacterData
{
    public int Id { get; set; }
    public int FashionId { get; set; }
    public int FashionColorId { get; set; }
    public int ChipFormId { get; set; }
    public int CreateTime { get; set; }
    public int StyleType { get; set; }
}

// Native XDlcChipData.
[MessagePackObject(true)]
public sealed class Theatre5DlcChipData
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Level { get; set; }
    public int Exp { get; set; }
    public int Breakthrough { get; set; }
    public bool IsLock { get; set; }
    public int CreateTime { get; set; }
}

// Native XWorldNpcPartData.
[MessagePackObject(true)]
public sealed class Theatre5WorldNpcPartData
{
    public List<Theatre5BigWorldCommanderFashion> PartList { get; set; } = [];
}

// Native BigWorldCommanderFashion.
[MessagePackObject(true)]
public sealed class Theatre5BigWorldCommanderFashion
{
    public int PartId { get; set; }
    public int ColourId { get; set; }
}

// Native XWorldPlayerBornData.
[MessagePackObject(true)]
public sealed class Theatre5WorldPlayerBornData
{
    public Theatre5DlcVector3? Position { get; set; }
    public Theatre5DlcVector3? EulerAngles { get; set; }
    public int LastLevelId { get; set; }
    public int LastWorldId { get; set; }
}

// Native XDlcVector3.
[MessagePackObject(true)]
public sealed class Theatre5DlcVector3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

// Native XDlcMultiplayerPlayerData.
[MessagePackObject(true)]
public sealed class Theatre5DlcMultiplayerPlayerData
{
    public int DlcMultiplayerTitle { get; set; }
    public int Camp { get; set; }
    public int DefenceCampCount { get; set; }
    public int CatSkillId { get; set; }
    public int MouseSkillId { get; set; }
    public List<int> SelectCatSkillIds { get; set; } = [];
    public List<int> SelectMouseSkillIds { get; set; } = [];
}

// Native XDlcRelinkPlayerData.
[MessagePackObject(true)]
public sealed class Theatre5DlcRelinkPlayerData
{
    public HashSet<int> PassedThisLevelIds { get; set; } = [];
    public List<int> EmojiWheelIds { get; set; } = [];
    public bool GlobalMatchEnabled { get; set; }
    public int GlobalMatchRewardTimes { get; set; }
}

// Native XWorldSaveData.
[MessagePackObject(true)]
public sealed class Theatre5WorldSaveData
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5LevelSaveData> LevelDataDict { get; set; } = [];
}

// Native XLevelSaveData.
[MessagePackObject(true)]
public sealed class Theatre5LevelSaveData
{
    public int WorldId { get; set; }
    public int LevelId { get; set; }
    public long OfflineTimeout { get; set; }
    public Theatre5DlcVector3? ReliablePos { get; set; }
    public float ReliableRotationY { get; set; }
    public Theatre5LevelActorSaveData? ActorSaveData { get; set; }
}

// Native XLevelActorSaveData.
[MessagePackObject(true)]
public sealed class Theatre5LevelActorSaveData
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5SceneObjectSaveData> SoSaveDatas { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5NpcSaveData> NpcSaveDatas { get; set; } = [];
}

// Native XSceneObjectSaveData.
[MessagePackObject(true)]
public sealed class Theatre5SceneObjectSaveData
{
    public bool Active { get; set; }
    public HashSet<int> Flags { get; set; } = [];
    public int MovementToNodeIndex { get; set; }
    public int MovementNodeIndexIncrement { get; set; }
    public bool? IsInteractable { get; set; }
    public int CurrentAction { get; set; }
}

// Native XNpcSaveData.
[MessagePackObject(true)]
public sealed class Theatre5NpcSaveData
{
    public bool? IsInteractable { get; set; }
    public int TipIconQuestId { get; set; }
    public Theatre5NpcRelativeFollowModeSaveData? RelativeFollowModeSaveData { get; set; }
    public Theatre5NpcDirectlyFollowModeSaveData? DirectlyFollowModeSaveData { get; set; }
    public Theatre5NpcGuideMoveSaveData? GuideMoveSaveData { get; set; }
    public Theatre5NpcNodeLockFollowModeSaveData? NodeLockFollowModeSaveData { get; set; }
}

// Native XNpcRelativeFollowModeSaveData.
[MessagePackObject(true)]
public sealed class Theatre5NpcRelativeFollowModeSaveData
{
    public bool? IsFollowPlayer { get; set; }
    public int FollowTargetNpcPlaceId { get; set; }
    public float TargetAngle { get; set; }
    public float TargetRadius { get; set; }
    public float ChaseRadius { get; set; }
    public float MaxIdleLagDistance { get; set; }
    public float IdleLookAtTargetDelayTime { get; set; }
    public bool? UseNavMesh { get; set; }
    public float NormalFollowRadius { get; set; }
    public float StartFollowDelayTime { get; set; }
}

// Native XNpcDirectlyFollowModeSaveData.
[MessagePackObject(true)]
public sealed class Theatre5NpcDirectlyFollowModeSaveData
{
    public bool? IsFollowPlayer { get; set; }
    public int FollowTargetNpcPlaceId { get; set; }
    public float MaxIdleRange { get; set; }
    public float StartFollowRange { get; set; }
    public bool? UseNavMesh { get; set; }
    public bool? IdleLookAtTarget { get; set; }
    public float ExpectedMovingRotateAngularSpeed { get; set; }
}

// Native XNpcGuideMoveSaveData.
[MessagePackObject(true)]
public sealed class Theatre5NpcGuideMoveSaveData
{
    public Theatre5DlcVector3? TargetPosition { get; set; }
    public bool? UseNavMesh { get; set; }
    public float OutOfRouteRange { get; set; }
    public float StartGuideRange { get; set; }
    public float ReachTargetPositionRange { get; set; }
    public string WaitDramaCaptionName { get; set; } = string.Empty;
    public float WaitDramaCaptionPlayProbability { get; set; }
    public float IdleTurningDelayTime { get; set; }
    public bool? GuideMoveTypeMatchesTarget { get; set; }
    public int GuideMoveType { get; set; }
}

// Native XNpcNodeLockFollowModeSaveData.
[MessagePackObject(true)]
public sealed class Theatre5NpcNodeLockFollowModeSaveData
{
    public bool? IsFollowMasterPlayer { get; set; }
    public int FollowTargetNpcPlaceId { get; set; }
    public string LockJointName { get; set; } = string.Empty;
    public Theatre5DlcVector3? PosOffset { get; set; }
}

// Native DlcQuestInfo.
[MessagePackObject(true)]
public sealed class Theatre5DlcQuestInfo
{
    public List<int> FinishedQuests { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5DlcQuest> ActiveQuests { get; set; } = [];
    public List<int> ReadyQuestIds { get; set; } = [];
}

// Native DlcQuest.
[MessagePackObject(true)]
public sealed class Theatre5DlcQuest
{
    public int QuestId { get; set; }
    public Theatre5DlcQuestDynamicData? DynamicData { get; set; }
    public Theatre5DlcQuestStaticData? StaticData { get; set; }
}

// Native DlcQuestDynamicData.
[MessagePackObject(true)]
public sealed class Theatre5DlcQuestDynamicData
{
    public int QuestState { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5DlcQuestStep> Steps { get; set; } = [];
    public HashSet<int> FinishedObjectiveIds { get; set; } = [];
    public int FixedSaveStepId { get; set; }
    public HashSet<int> OccupiedObjectIds { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5LevelActorSaveData> LevelActorSaves { get; set; } = [];
    public List<int> TrialCharacterIdList { get; set; } = [];
    public int TrialCharacterAddMode { get; set; }
    public int TrialCurCharacterPos { get; set; }
    public Theatre5DlcVarBlockData? VarBlockData { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> FuncEntryDisableDict { get; set; } = [];
    public List<Theatre5DlcSystemFunctionControlSave> SystemFuncControlSaveData { get; set; } = [];
}

// Native DlcQuestStep.
[MessagePackObject(true)]
public sealed class Theatre5DlcQuestStep
{
    public int StepId { get; set; }
    public int StepState { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5DlcQuestStepObjective> Objectives { get; set; } = [];
}

// Native DlcQuestStepObjective.
[MessagePackObject(true)]
public sealed class Theatre5DlcQuestStepObjective
{
    public int Id { get; set; }
    public int ObjectiveState { get; set; }
    public int FinishType { get; set; }
    public Theatre5DlcLevelActionListData? EnterActionListData { get; set; }
    public Theatre5DlcLevelActionListData? ExitActionListData { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5DlcQuestNavPointSaveData> NavPointData { get; set; } = [];
    public int InstLevelCompleteCount { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, List<Theatre5DlcQuestInteractRecord>> InteractProgressRecords { get; set; } = [];
    public List<int> NarrativeCompletedRecords { get; set; } = [];
    public List<int> SceneObjectCollectedRecords { get; set; } = [];
    public int SceneObjectCollectedCountRecord { get; set; }
}

// Native DlcLevelActionListData.
[MessagePackObject(true)]
public sealed class Theatre5DlcLevelActionListData
{
    public int CurActionIndex { get; set; }
    public int CurActionState { get; set; }
}

// Native DlcQuestNavPointSaveData.
[MessagePackObject(true)]
public sealed class Theatre5DlcQuestNavPointSaveData
{
    public int NavPointConfigId { get; set; }
    public int? HideFlags { get; set; }
}

// Native DlcQuestInteractRecord.
[MessagePackObject(true)]
public sealed class Theatre5DlcQuestInteractRecord
{
    public int TargetPlaceId { get; set; }
    public bool WasCompleted { get; set; }
}

// Native XDlcVarBlockData.
[MessagePackObject(true)]
public sealed class Theatre5DlcVarBlockData
{
    public Dictionary<string, int> IntDict { get; set; } = [];
    public Dictionary<string, float> FloatDict { get; set; } = [];
    public Dictionary<string, bool> BoolDict { get; set; } = [];
    public Dictionary<string, Theatre5DlcVector2> Vector2Dict { get; set; } = [];
    public Dictionary<string, Theatre5DlcVector3> Vector3Dict { get; set; } = [];
}

// Native XDlcVector2.
[MessagePackObject(true)]
public sealed class Theatre5DlcVector2
{
    public float X { get; set; }
    public float Y { get; set; }
}

// Native DlcSystemFunctionControlSave.
[MessagePackObject(true)]
public sealed class Theatre5DlcSystemFunctionControlSave
{
    public int SystemFunctionType { get; set; }
    public int LimitType { get; set; }
    public bool IsLimit { get; set; }
}

// Native DlcQuestStaticData.
[MessagePackObject(true)]
public sealed class Theatre5DlcQuestStaticData
{
}

// Native XAutoChessGameplayData.
[MessagePackObject(true)]
public sealed class Theatre5AutoChessGameplayData
{
    public int RoundNum { get; set; }
    public Theatre5AutoChessNpcData? SelfData { get; set; }
    public Theatre5AutoChessNpcData? EnemyData { get; set; }
}

// Native XAutoChessNpcData.
[MessagePackObject(true)]
public sealed class Theatre5AutoChessNpcData
{
    public int TemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int HeadFrameId { get; set; }
    public Theatre5AutoChessData? AutoChessData { get; set; }
}

// Native XAutoChessData.
[MessagePackObject(true)]
public sealed class Theatre5AutoChessData
{
    public int CharacterId { get; set; }
    public int CharacterLevel { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Attribs { get; set; } = [];
    public List<int> Skills { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> MagicIds { get; set; } = [];
    public int FashionId { get; set; }
    public int[] WeaponIds { get; set; } = [];
    public List<Theatre5RuneEvolve> RuneEvolves { get; set; } = [];
    public List<int> Relics { get; set; } = [];
}

// Native RuneEvolve.
[MessagePackObject(true)]
public sealed class Theatre5RuneEvolve
{
    public int RuneId { get; set; }
    public bool IsStrengthen { get; set; }
}

// Native XTheatre6GameplayData.
[MessagePackObject(true)]
public sealed class Theatre5Theatre6GameplayData
{
    public Theatre5Theatre6NpcData? SelfData { get; set; }
    public Theatre5Theatre6NpcData? EnemyData { get; set; }
}

// Native XTheatre6NpcData.
[MessagePackObject(true)]
public sealed class Theatre5Theatre6NpcData
{
    public int TemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int HeadFrameId { get; set; }
    public int CharacterId { get; set; }
    public int CharacterLevel { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Attribs { get; set; } = [];
    public List<int> Skills { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> MagicIds { get; set; } = [];
    public int FashionId { get; set; }
    public int[] WeaponIds { get; set; } = [];
    public List<int> Relics { get; set; } = [];
}

// Native XDlcFightResultData.
[MessagePackObject(true)]
public sealed class Theatre5DlcFightResultData
{
    public bool IsPlayerWin { get; set; }
    public int FinishTime { get; set; }
    public int ChapterId { get; set; }
    public Theatre5WorldData? WorldData { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5DlcFightResultPlayerData> PlayerData { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, long> ReJoinWorldExpireTime { get; set; } = [];
    public long StartFightTime { get; set; }
    public long SettleTime { get; set; }
    public int SettleState { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5DlcNpcSettleInfo> NpcSettleInfos { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5DlcCustomFightResultData> CustomData { get; set; } = [];
    public string DlcMatchServerNodeId { get; set; } = string.Empty;
    public string FightUid { get; set; } = string.Empty;
    public byte[] RoomData { get; set; } = [];
    public string RoomId { get; set; } = string.Empty;
    public bool OpenCheatDetector { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5MouseHunterRecordData> RecordDataDict { get; set; } = [];
    public Theatre5AutoChessCheckData? AutoChessCheckData { get; set; }
}

// Native XDlcFightResultPlayerData.
[MessagePackObject(true)]
public sealed class Theatre5DlcFightResultPlayerData
{
    public int PlayerId { get; set; }
    public int Level { get; set; }
    public bool IsWin { get; set; }
    public bool IsSettled { get; set; }
    public int SettleTime { get; set; }
    public int DeathCount { get; set; }
    public long HitHurt { get; set; }
    public int ResurrectionCount { get; set; }
    public int ResurrectionPlayerCount { get; set; }
    public int State { get; set; }
    public int HealthPercentage { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, Theatre5DlcBossSettlementData> BossSettlementData { get; set; } = [];
    public List<int> UnlockBadge { get; set; } = [];
    public Theatre5DlcMultiplayerResultPlayerData? MultiplayerResultPlayerData { get; set; }
    public Theatre5DlcRelinkResultData? RelinkResultData { get; set; }
}

// Native XDlcBossSettlementData.
[MessagePackObject(true)]
public sealed class Theatre5DlcBossSettlementData
{
    public int Id { get; set; }
    public int TotalHurt { get; set; }
    public int TotalPartHurts { get; set; }
    public int PartBreakCount { get; set; }
    public int ControlCount { get; set; }
    public bool LastHit { get; set; }
    public bool HasBrokeAnyPart { get; set; }
}

// Native XDlcMultiplayerResultPlayerData.
[MessagePackObject(true)]
public sealed class Theatre5DlcMultiplayerResultPlayerData
{
    public int DlcMultiplayerTitle { get; set; }
    public int CharacterId { get; set; }
}

// Native XDlcRelinkResultData.
[MessagePackObject(true)]
public sealed class Theatre5DlcRelinkResultData
{
    public List<Theatre5DlcRelinkReward> XDlcRelinkRewards { get; set; } = [];
    public bool GetGlobalMatchReward { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> DestroyPart2CountDict { get; set; } = [];
    public int EquLevel { get; set; }
    public int Score { get; set; }
    public HashSet<int> BattleTitle { get; set; } = [];
    public int CharacterId { get; set; }
    public int StyleType { get; set; }
    public bool IsCommitter { get; set; }
}

// Native XDlcRelinkReward.
[MessagePackObject(true)]
public sealed class Theatre5DlcRelinkReward
{
    public int Id { get; set; }
    public int Type { get; set; }
    public int Count { get; set; }
}

// Native XDlcNpcSettleInfo.
[MessagePackObject(true)]
public sealed class Theatre5DlcNpcSettleInfo
{
    public int TemplateId { get; set; }
    public int NpcId { get; set; }
    public int LeftHp { get; set; }
    public bool IsPlayer { get; set; }
    public bool IsBoss { get; set; }
}

// Native XDlcCustomFightResultData.
[MessagePackObject(true)]
public sealed class Theatre5DlcCustomFightResultData
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> Dict { get; set; } = [];
}

// Native XMouseHunterRecordData.
[MessagePackObject(true)]
public sealed class Theatre5MouseHunterRecordData
{
    public List<float> TimingsOfBehaviors { get; set; } = [];
    public List<string> PositionsOfBehaviors { get; set; } = [];
    public List<string> PositionsByTimeInterval { get; set; } = [];
    public List<int> Behaviors { get; set; } = [];
    public List<string> DetailsOfBehaviors { get; set; } = [];
    public int PlayerId { get; set; }
    public int LevelId { get; set; }
    public int WorldId { get; set; }
}

// Native XAutoChessCheckData.
[MessagePackObject(true)]
public sealed class Theatre5AutoChessCheckData
{
    public long ActBattleTime { get; set; }
    public bool UsePause { get; set; }
    public bool UseDoubleSpeed { get; set; }
    public Theatre5AutoChessNpcRecordData? MyData { get; set; }
    public Theatre5AutoChessNpcRecordData? EnemyData { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ConditionParam { get; set; } = [];
}

// Native XAutoChessNpcRecordData.
[MessagePackObject(true)]
public sealed class Theatre5AutoChessNpcRecordData
{
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> GemRecord { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> SkillDamageRecord { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> SkillCureRecord { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> ProtectorRecord { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> RateRecord { get; set; } = [];
    public Theatre5AutoChessRecordData? RecordData { get; set; }
    public int TotalDamage { get; set; }
    public int TotalCure { get; set; }
    public int TotalProtector { get; set; }
}

// Native XAutoChessRecordData.
[MessagePackObject(true)]
public sealed class Theatre5AutoChessRecordData
{
    [BsonSerializer(typeof(Theatre5NestedIntMapSerializer))]
    public Dictionary<int, Dictionary<int, int>> DamageRecord { get; set; } = [];
    [BsonSerializer(typeof(Theatre5NestedIntMapSerializer))]
    public Dictionary<int, Dictionary<int, int>> CureRecord { get; set; } = [];
    [BsonSerializer(typeof(Theatre5NestedIntMapSerializer))]
    public Dictionary<int, Dictionary<int, int>> ProtectorRecord { get; set; } = [];
}

// Native XAutoChessGameplayResult.
[MessagePackObject(true)]
public sealed class Theatre5AutoChessGameplayResult
{
    public int CheckFailTimes { get; set; }
    public int RoundNum { get; set; }
    public bool IsFinish { get; set; }
    public double ProcessRating { get; set; }
    public int TrophyNum { get; set; }
    public int Health { get; set; }
    public bool IsPvpExtra { get; set; }
    public bool IsPvpExtraChoice { get; set; }
    public int NormalOriginRating { get; set; }
    public int Rating { get; set; }
    public int RankProtectNum { get; set; }
    public int Rank { get; set; }
    public int BeforeStoryEntranceId { get; set; }
    public Theatre5PveStoryLineData? PveStoryLineData { get; set; }
    public Theatre5PveChapterData? PveChapterData { get; set; }
    public Theatre5PveRewardShow? RewardShow { get; set; }
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> CommonFightCnt { get; set; } = [];
    public int AddExp { get; set; }
    public bool IsCanFreeUnlockGrid { get; set; }
}

// Native XTheatre5PveStoryLineData.
[MessagePackObject(true)]
public sealed class Theatre5PveStoryLineData
{
    public int StoryLineId { get; set; }
    public int CurContentId { get; set; }
    public List<int> FinishContents { get; set; } = [];
    public HashSet<int> HandleEvents { get; set; } = [];
}

// Native XTheatre5PveChapterData.
[MessagePackObject(true)]
public sealed class Theatre5PveChapterData
{
    public int ChapterId { get; set; }
    public Theatre5PveChapterLevelData? CurPveChapterLevel { get; set; }
    public List<int> HasSelectedEvents { get; set; } = [];
    public List<int> HasRandomEvents { get; set; } = [];
    public List<bool> BattleStatus { get; set; } = [];
    public int ContinueWin { get; set; }
    public HashSet<int> HandleEvents { get; set; } = [];
    public int BuyCnt { get; set; }
}

// Native XTheatre5PveChapterLevelData.
[MessagePackObject(true)]
public sealed class Theatre5PveChapterLevelData
{
    public int Level { get; set; }
    public List<int> RandomEvents { get; set; } = [];
    public List<int> RunEvents { get; set; } = [];
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> RandomEventCnt { get; set; } = [];
}

// Native XTheatre5PveRewardShow.
[MessagePackObject(true)]
public sealed class Theatre5PveRewardShow
{
    public bool IsWin { get; set; }
    public int TotalCoin { get; set; }
    public int BaseRewardCoin { get; set; }
    public int LevelRewardCoin { get; set; }
    public int FinishLevel { get; set; }
    public int HpRewardCoin { get; set; }
    public int LeftHp { get; set; }
    public int LossRewardFactor { get; set; }
}

// Native DlcReportWorldResult.
[MessagePackObject(true)]
public sealed class Theatre5DlcReportWorldResult
{
    public Theatre5DlcFightResultData? DlcFightSettleData { get; set; }
}

// Both integer-keyed levels require an array representation in BSON; the wire stays maps.
public sealed class Theatre5NestedIntMapSerializer
    : DictionaryInterfaceImplementerSerializer<Dictionary<int, Dictionary<int, int>>, int, Dictionary<int, int>>
{
    public Theatre5NestedIntMapSerializer()
        : base(DictionaryRepresentation.ArrayOfDocuments, new Int32Serializer(),
            new DictionaryInterfaceImplementerSerializer<Dictionary<int, int>, int, int>(DictionaryRepresentation.ArrayOfDocuments))
    {
    }
}
