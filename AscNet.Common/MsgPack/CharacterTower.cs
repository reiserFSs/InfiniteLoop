using MessagePack;

namespace AscNet.Common.MsgPack;

// EN client schema: lua/matrix/xmanager/XFubenCharacterTowerManager.lua NotifyLoginCharacterTowerData /
// NotifyActivityCharacterTowerData and xentity/xcharactertower/XCharacterTowerChapterInfo.lua,
// XCharacterTowerRelationInfo.lua. ChapterInfos and every nested list are iterated unguarded by the client,
// so all lists are non-null here.

[MessagePackObject(true)]
public sealed class CharacterTowerRelationInfo
{
    public int RelationId { get; set; }
    public List<int> FightEventIds { get; set; } = [];
    public List<string> StoryIds { get; set; } = [];
    public List<int> FinishConditions { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class CharacterTowerChapterInfo
{
    public int ChapterId { get; set; }
    public List<int> ChapterRewardData { get; set; } = [];
    public List<int> TreasureData { get; set; } = [];
    public List<int> StageRewardData { get; set; } = [];
    public List<int> VideoedIds { get; set; } = [];
    public List<CharacterTowerRelationInfo> RelationInfos { get; set; } = [];
    public List<int> TriggerConditions { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class CharacterTowerDataDb
{
    public List<CharacterTowerChapterInfo> ChapterInfos { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyLoginCharacterTowerData
{
    public CharacterTowerDataDb CharacterTowerDataDb { get; set; } = new();
    public List<int> ActivityChapters { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyActivityCharacterTowerData
{
    public List<int> ActivityChapters { get; set; } = [];
}
