using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

/// <summary>
/// Arcade Anima (CharacterTower) durable claim/relation metadata. Stage progression is not stored here:
/// Stage.Stages remains the only Passed/StarsMark authority, and reward delivery is anchored by the stable
/// claim receipts written by RewardHandler.ApplyRewardsOnceAndPersist.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class CharacterTowerRelationState
{
    [BsonElement("relation_id")] public int RelationId { get; set; }
    [BsonElement("fight_event_ids")] public List<int> FightEventIds { get; set; } = [];
    [BsonElement("story_ids")] public List<string> StoryIds { get; set; } = [];
    [BsonElement("finish_conditions")] public List<int> FinishConditions { get; set; } = [];
}

[BsonIgnoreExtraElements]
public sealed class CharacterTowerChapterState
{
    [BsonElement("chapter_id")] public int ChapterId { get; set; }
    [BsonElement("chapter_reward_data")] public List<int> ChapterRewardData { get; set; } = [];
    [BsonElement("treasure_data")] public List<int> TreasureData { get; set; } = [];
    [BsonElement("stage_reward_data")] public List<int> StageRewardData { get; set; } = [];
    [BsonElement("videoed_ids")] public List<int> VideoedIds { get; set; } = [];
    [BsonElement("relations")] public List<CharacterTowerRelationState> Relations { get; set; } = [];
    [BsonElement("trigger_conditions")] public List<int> TriggerConditions { get; set; } = [];
}

[BsonIgnoreExtraElements]
public sealed class CharacterTowerState
{
    [BsonElement("chapters")] public List<CharacterTowerChapterState> Chapters { get; set; } = [];
}

public partial class Player
{
    [BsonElement("character_tower")] public CharacterTowerState CharacterTower { get; set; } = new();
}
