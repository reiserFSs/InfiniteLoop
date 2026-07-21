using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("dorm")]
    public PlayerDormState Dorm { get; set; } = new();
}

public sealed class PlayerDormState
{
    [BsonElement("rooms")]
    public List<PlayerDormRoom> Rooms { get; set; } = new();

    [BsonElement("characters")]
    public List<PlayerDormCharacter> Characters { get; set; } = new();

    [BsonElement("work_list")]
    public List<PlayerDormWork> WorkList { get; set; } = new();

    [BsonElement("pending_rewards")]
    public List<PlayerDormPendingReward> PendingRewards { get; set; } = new();

    [BsonElement("furniture")]
    public List<PlayerDormFurniture> Furniture { get; set; } = new();


    [BsonElement("furniture_create_list")]
    public List<PlayerDormFurnitureCreate> FurnitureCreateList { get; set; } = new();

    [BsonElement("furniture_unlocks")]
    public List<uint> FurnitureUnlocks { get; set; } = new();

    [BsonElement("layouts")]
    public List<PlayerDormLayout> Layouts { get; set; } = new();

    [BsonElement("bind_relations")]
    public List<PlayerDormBindRelation> BindRelations { get; set; } = new();

    [BsonElement("shares")]
    public List<PlayerDormShare> Shares { get; set; } = new();

    [BsonElement("visits")]
    public List<PlayerDormVisit> Visits { get; set; } = new();

    [BsonElement("quest")]
    public PlayerDormQuestState Quest { get; set; } = new();

    [BsonElement("next_furniture_id")]
    public int NextFurnitureId { get; set; } = 1;

    [BsonElement("next_layout_id")]
    public int NextLayoutId { get; set; } = 10001;

    [BsonElement("last_snapshot_time")]
    public uint LastSnapshotTime { get; set; }

    [BsonElement("last_recommend_time")]
    public uint LastRecommendTime { get; set; }

    [BsonElement("work_next_refresh_time")]
    public uint WorkNextRefreshTime { get; set; }

    [BsonElement("snapshot_times")]
    public int SnapshotTimes { get; set; }

    public bool NormalizeFurnitureIds()
    {
        int maximum = Furniture.Concat(FurnitureCreateList.Select(entry => entry.Furniture).OfType<PlayerDormFurniture>())
            .Select(entry => entry.Id).DefaultIfEmpty(0).Max();
        int next = checked(Math.Max(0, maximum) + 1);
        if (NextFurnitureId >= next) return false;
        NextFurnitureId = next;
        return true;
    }

}

public sealed class PlayerDormRoom
{
    [BsonElement("id")] public uint Id { get; set; }
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
}

public sealed class PlayerDormCharacter
{
    [BsonElement("character_id")] public uint CharacterId { get; set; }
    [BsonElement("dormitory_id")] public int DormitoryId { get; set; } = -1;
    [BsonElement("mood")] public int Mood { get; set; }
    [BsonElement("vitality")] public int Vitality { get; set; }
    [BsonElement("mood_speed")] public int MoodSpeed { get; set; }
    [BsonElement("vitality_speed")] public int VitalitySpeed { get; set; }
    [BsonElement("last_fondle_recovery_time")] public uint LastFondleRecoveryTime { get; set; }
    [BsonElement("left_fondle_count")] public int LeftFondleCount { get; set; }
    [BsonElement("last_recovery_time")] public uint LastRecoveryTime { get; set; }
    [BsonElement("event_list")] public List<PlayerDormEvent> EventList { get; set; } = new();
}

public sealed class PlayerDormEvent
{
    [BsonElement("event_id")] public int EventId { get; set; }
    [BsonElement("end_time")] public uint EndTime { get; set; }
}

public sealed class PlayerDormWork
{
    [BsonElement("character_id")] public uint CharacterId { get; set; }
    [BsonElement("work_pos")] public int WorkPos { get; set; }
    [BsonElement("work_end_time")] public uint WorkEndTime { get; set; }
    [BsonElement("dormitory_num")] public int DormitoryNum { get; set; }
    [BsonElement("reward_num")] public int RewardNum { get; set; }
    [BsonElement("reset_count")] public int ResetCount { get; set; }
    [BsonElement("claim_key")] public string ClaimKey { get; set; } = string.Empty;
}

public sealed class PlayerDormPendingReward
{
    [BsonElement("key")] public string Key { get; set; } = string.Empty;
    [BsonElement("goods")] public List<PlayerDormPendingRewardItem> Goods { get; set; } = new();
}

public sealed class PlayerDormPendingRewardItem
{
    [BsonElement("id")] public int Id { get; set; }
    [BsonElement("template_id")] public int TemplateId { get; set; }
    [BsonElement("count")] public int Count { get; set; }
    [BsonElement("params")] public List<int> Params { get; set; } = new();
}

public sealed class PlayerDormFurniture
{
    [BsonElement("id")] public int Id { get; set; }
    [BsonElement("config_id")] public uint ConfigId { get; set; }
    [BsonElement("x")] public int X { get; set; }
    [BsonElement("y")] public int Y { get; set; }
    [BsonElement("angle")] public int Angle { get; set; }
    [BsonElement("dormitory_id")] public int DormitoryId { get; set; } = -1;
    [BsonElement("addition")] public int Addition { get; set; }
    [BsonElement("attr_list")] public List<int> AttrList { get; set; } = new();
    [BsonElement("base_attr_list")] public List<int> BaseAttrList { get; set; } = new();
    [BsonElement("is_locked")] public bool IsLocked { get; set; }
}


public sealed class PlayerDormFurnitureCreate
{
    [BsonElement("pos")] public int Pos { get; set; }
    [BsonElement("end_time")] public uint EndTime { get; set; }
    [BsonElement("furniture")] public PlayerDormFurniture? Furniture { get; set; }
    [BsonElement("count")] public int Count { get; set; }
}

public sealed class PlayerDormLayoutFurniture
{
    [BsonElement("config_id")] public uint ConfigId { get; set; }
    [BsonElement("x")] public int X { get; set; }
    [BsonElement("y")] public int Y { get; set; }
    [BsonElement("angle")] public int Angle { get; set; }
}

public sealed class PlayerDormLayout
{
    [BsonElement("layout_id")] public int LayoutId { get; set; }
    [BsonElement("create_time")] public uint CreateTime { get; set; }
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("furniture_list")] public List<PlayerDormLayoutFurniture> FurnitureList { get; set; } = new();
}

public sealed class PlayerDormBindRelation
{
    [BsonElement("layout_id")] public int LayoutId { get; set; }
    [BsonElement("dormitory_id")] public int DormitoryId { get; set; }
}

public sealed class PlayerDormShare
{
    [BsonElement("share_id")] public string ShareId { get; set; } = string.Empty;
    [BsonElement("create_time")] public uint CreateTime { get; set; }
    [BsonElement("furniture_list")] public List<PlayerDormLayoutFurniture> FurnitureList { get; set; } = new();
}

public sealed class PlayerDormVisit
{
    [BsonElement("player_id")] public long PlayerId { get; set; }
    [BsonElement("visit_time")] public uint VisitTime { get; set; }
}

public sealed class PlayerDormQuestState
{
    [BsonElement("reset_count")] public int ResetCount { get; set; }
    [BsonElement("terminal_lv")] public int TerminalLv { get; set; } = 1;
    [BsonElement("terminal_upgrade_exp")] public int TerminalUpgradeExp { get; set; }
    [BsonElement("finish_quest_count")] public int FinishQuestCount { get; set; }
    [BsonElement("terminal_upgrade_time")] public uint TerminalUpgradeTime { get; set; }
    [BsonElement("terminal_upgrade_status")] public int TerminalUpgradeStatus { get; set; }
    [BsonElement("finish_quests")] public List<List<int>> FinishQuests { get; set; } = new();
    [BsonElement("trigger_limited_quest")] public List<int> TriggerLimitedQuest { get; set; } = new();
    [BsonElement("total_quest")] public List<PlayerDormQuest> TotalQuest { get; set; } = new();
    [BsonElement("quest_accept")] public List<PlayerDormQuestAccept> QuestAccept { get; set; } = new();
    [BsonElement("collect_files")] public List<PlayerDormCollectFile> CollectFiles { get; set; } = new();
}

public sealed class PlayerDormQuest
{
    [BsonElement("quest_id")] public int QuestId { get; set; }
    [BsonElement("file_id")] public int FileId { get; set; }
    [BsonElement("index")] public int Index { get; set; }
    [BsonElement("is_special_quest")] public bool IsSpecialQuest { get; set; }
    [BsonElement("reset_count")] public int ResetCount { get; set; }
}

public sealed class PlayerDormQuestAccept
{
    [BsonElement("quest_id")] public int QuestId { get; set; }
    [BsonElement("accept_time")] public uint AcceptTime { get; set; }
    [BsonElement("team_character")] public List<uint> TeamCharacter { get; set; } = new();
    [BsonElement("file_id")] public int FileId { get; set; }
    [BsonElement("is_special_quest")] public bool IsSpecialQuest { get; set; }
    [BsonElement("index")] public int Index { get; set; }
    [BsonElement("is_satisfy_recommend")] public bool IsSatisfyRecommend { get; set; }
    [BsonElement("reset_count")] public int ResetCount { get; set; }
    [BsonElement("is_award")] public bool IsAward { get; set; }
}

public sealed class PlayerDormCollectFile
{
    [BsonElement("file_id")] public int FileId { get; set; }
    [BsonElement("is_read")] public bool IsRead { get; set; }
}
