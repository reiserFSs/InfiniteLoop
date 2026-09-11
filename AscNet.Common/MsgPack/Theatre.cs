using MessagePack;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;

namespace AscNet.Common.MsgPack;

// int models client-consumed integral values, not a proven retail MessagePack integer width.
// PassEventRecord bool leaves are an approved local wire choice; Lua proves only truthy presence.
// Slot StoryId uses the authored string domain; its Lua consumer alone does not establish encoding.

[MessagePackObject(true)]
public class TheatreResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public class TheatreData
{
    public List<int> Decorations { get; set; } = [];
    public List<int> UnlockPowerIds { get; set; } = [];
    public List<int> UnlockPowerFavorIds { get; set; } = [];
    public List<int> EffectPowerFavorIds { get; set; } = [];
    public List<int> SkillIllustratedBook { get; set; } = [];
    public List<TheatreKeepsake> Keepsakes { get; set; } = [];
    public List<int> PassChapterId { get; set; } = [];
    public List<int> EndingRecord { get; set; } = [];
    [BsonSerializer(typeof(TheatreEventRecordSerializer))]
    public Dictionary<int, Dictionary<int, bool>> PassEventRecord { get; set; } = [];
    public TheatreChapterDb? CurChapterDb { get; set; }
    public int DifficultyId { get; set; }
    public int KeepsakeId { get; set; }
    public int CurRoleLv { get; set; }
    public int ReopenCount { get; set; }
    public List<int> Skills { get; set; } = [];
    public List<int> RecruitRole { get; set; } = [];
    public int UseOwnCharacter { get; set; }
    public int FavorCoin { get; set; }
    public int DecorationCoin { get; set; }
    public int PassNodeCount { get; set; }
    public TheatreTeamData? SingleTeamData { get; set; }
    public List<TheatreTeamData> MultiTeamDatas { get; set; } = [];
}

// BSON needs array representations at both integer-keyed levels; MessagePack stays a nested map.
public sealed class TheatreEventRecordSerializer
    : DictionaryInterfaceImplementerSerializer<Dictionary<int, Dictionary<int, bool>>, int, Dictionary<int, bool>>
{
    public TheatreEventRecordSerializer()
        : base(DictionaryRepresentation.ArrayOfDocuments, new Int32Serializer(),
            new DictionaryInterfaceImplementerSerializer<Dictionary<int, bool>, int, bool>(DictionaryRepresentation.ArrayOfDocuments))
    {
    }
}

[MessagePackObject(true)]
public sealed class TheatreChapterDb
{
    public int ChapterId { get; set; }
    public int RefreshRoleCount { get; set; }
    public int PassNodeCount { get; set; }
    public List<int> RefreshRole { get; set; } = [];
    public List<int> SkillToSelect { get; set; } = [];
    public TheatreNodeData CurNodeDb { get; set; } = new();
}

[MessagePackObject(true)]
public class TheatreNodeData
{
    public int NodeId { get; set; }
    public List<TheatreSlot> Slots { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class TheatreSlot
{
    public int SlotId { get; set; }
    public int SlotType { get; set; }
    public int Selected { get; set; }
    public int ConfigId { get; set; }
    public int CurStepId { get; set; }
    public List<TheatreShopItem> ShopItems { get; set; } = [];
    public int TheatreStageId { get; set; }
    public int RewardType { get; set; }
    public int PowerId { get; set; }
    public List<int> StageIds { get; set; } = [];
    public List<int> PassedStageIds { get; set; } = [];
    public List<int> PassedStageIndexs { get; set; } = [];
    public string StoryId { get; set; } = string.Empty;
}

[MessagePackObject(true)]
public sealed class TheatreShopItem
{
    public int ItemType { get; set; }
    public int Count { get; set; }
    public int Price { get; set; }
    public int IsBuy { get; set; }
    public int PowerId { get; set; }
    public List<int> Skills { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class TheatreTeamData
{
    public int TeamIndex { get; set; }
    public int CaptainPos { get; set; }
    public int FirstFightPos { get; set; }
    public int EnterCgIndex { get; set; }
    public int SettleCgIndex { get; set; }
    public List<int> CardIds { get; set; } = [0, 0, 0];
    public List<int> RobotIds { get; set; } = [0, 0, 0];
}

[MessagePackObject(true)]
public class TheatreKeepsake
{
    public int KeepsakeId { get; set; }
    public int Lv { get; set; }
    public int FightCount { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreAdventureSettleData
{
    public int Ending { get; set; }
    public bool NewEnding { get; set; }
    public bool NewRecord { get; set; }
    public int TotalPoint { get; set; }
    public int SettleNodeCount { get; set; }
    public int SettleNodeCountPoint { get; set; }
    public int SettleFightCount { get; set; }
    public int SettleFightCountPoint { get; set; }
    public int SettleEventCount { get; set; }
    public int SettleEventCountPoint { get; set; }
    public int SettleBossCount { get; set; }
    public int SettleBossCountPoint { get; set; }
    public int SettleLeftReopenCount { get; set; }
    public int SettleLeftReopenCountPoint { get; set; }
    public int FavorCoin { get; set; }
    public int DecorationCoin { get; set; }
    public List<int> UnlockPowerFavorIds { get; set; } = [];
    public List<int> PassChapterId { get; set; } = [];
    [BsonSerializer(typeof(TheatreEventRecordSerializer))]
    public Dictionary<int, Dictionary<int, bool>> PassEventRecord { get; set; } = [];
    public List<int> EndingRecord { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class TheatreStartAdventureRequest
{
    public int Difficulty { get; set; }
    public int? KeepsakeId { get; set; }
    public int Mode { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreStartAdventureResponse : TheatreResponse
{
    public int ChapterId { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreSettleAdventureRequest { }

[MessagePackObject(true)]
public sealed class TheatreSettleAdventureResponse : TheatreResponse
{
    public TheatreAdventureSettleData SettleData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class TheatreRefreshCharacterRequest { }

[MessagePackObject(true)]
public sealed class TheatreRefreshCharacterResponse : TheatreResponse
{
    public int RefreshRoleCount { get; set; }
    public List<int> RoleList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class TheatreRecruitCharacterRequest
{
    public int RoleId { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreRecruitCharacterResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreSelectNodeRequest
{
    public int NodeId { get; set; }
    public int SlotId { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreSelectNodeResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreEndNodeRequest { }

[MessagePackObject(true)]
public sealed class TheatreEndNodeResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreEventNodeNextStepRequest
{
    public int CurStepId { get; set; }
    public int? OptionId { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreEventNodeNextStepResponse : TheatreResponse
{
    public int NextStepId { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class TheatreNodeShopOpenSkillRequest { }

[MessagePackObject(true)]
public sealed class TheatreNodeShopOpenSkillResponse : TheatreResponse
{
    public List<int> Skills { get; set; } = [];
    public int PowerId { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreNodeShopBuyItemRequest
{
    public int Type { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreNodeShopBuyItemResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreSelectSkillRequest
{
    public int SkillId { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreSelectSkillResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreSkipSelectSkillRequest { }

[MessagePackObject(true)]
public sealed class TheatreSkipSelectSkillResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreSetSingleTeamRequest
{
    public TheatreTeamData TeamData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class TheatreSetSingleTeamResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreSetMultiTeamRequest
{
    public List<TheatreTeamData> TeamDatas { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class TheatreSetMultiTeamResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreMultiTeamResetRequest
{
    public int TeamIndex { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreMultiTeamResetResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreDecorationUpgradeRequest
{
    public int DecorationId { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreDecorationUpgradeResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatrePowerFavorUpgradeRequest
{
    public int PowerFavorId { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatrePowerFavorUpgradeResponse : TheatreResponse { }

[MessagePackObject(true)]
public sealed class TheatreGetPowerFavorRewardRequest
{
    public int PowerFavorId { get; set; }
}

[MessagePackObject(true)]
public sealed class TheatreGetPowerFavorRewardResponse : TheatreResponse
{
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyTheatreData : TheatreData { }

[MessagePackObject(true)]
public sealed class NotifyTheatreKeepsakeUpgrade : TheatreKeepsake { }

[MessagePackObject(true)]
public sealed class NotifyUnlockKeepsake
{
    public TheatreKeepsake Keepsake { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatreNodeReward
{
    public int RewardType { get; set; }
    public List<int> Skills { get; set; } = [];
    public int Lv { get; set; }
    public int DecorationPoint { get; set; }
    public int FavorPoint { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatreAddNode : TheatreNodeData
{
    public int ChapterId { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatreAdventureSettle
{
    public TheatreAdventureSettleData SettleData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class NotifyTheatreChapterSettle
{
    public TheatreAdventureSettleData SettleData { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class TheatreNodeNextStep
{
    public int EventId { get; set; }
    public int NextStepId { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatreReopenCount
{
    public int ReopenCount { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatreUseOwnCharacter
{
    public int UseOwnCharacter { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyTheatreCoinChange
{
    public int FavorCoin { get; set; }
    public int DecorationCoin { get; set; }
}
