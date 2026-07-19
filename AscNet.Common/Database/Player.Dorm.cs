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

    [BsonElement("furniture")]
    public List<PlayerDormFurniture> Furniture { get; set; } = new();

    [BsonElement("snapshot_times")]
    public int SnapshotTimes { get; set; }
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
}

public sealed class PlayerDormFurniture
{
    [BsonElement("id")] public int Id { get; set; }
    [BsonElement("config_id")] public uint ConfigId { get; set; }
    [BsonElement("x")] public int X { get; set; }
    [BsonElement("y")] public int Y { get; set; }
    [BsonElement("angle")] public int Angle { get; set; }
    [BsonElement("dormitory_id")] public int DormitoryId { get; set; }
    [BsonElement("addition")] public int Addition { get; set; }
    [BsonElement("attr_list")] public List<int> AttrList { get; set; } = new();
    [BsonElement("base_attr_list")] public List<int> BaseAttrList { get; set; } = new();
    [BsonElement("is_locked")] public bool IsLocked { get; set; }
}
