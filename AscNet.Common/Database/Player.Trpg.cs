using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

/// <summary>Main-line chapter 13 (Fake Ascension) TRPG progress. Normal-mode stage passes live in the stage
/// document and are derived from it; this holds what only the TRPG layer knows.</summary>
[BsonIgnoreExtraElements]
public sealed class TrpgState
{
    /// <summary>0 = never chosen; login then sends the first main-line target chain.</summary>
    [BsonElement("cur_target_link")] public int CurTargetLink { get; set; }
    [BsonElement("targets")] public List<int> Targets { get; set; } = new();
    [BsonElement("rewards")] public List<int> Rewards { get; set; } = new();
    [BsonElement("functions")] public List<int> Functions { get; set; } = new();
    [BsonElement("memoirs")] public List<int> Memoirs { get; set; } = new();
    /// <summary>False = exploration page, true = regular (normal) main line page.</summary>
    [BsonElement("is_normal_page")] public bool IsNormalPage { get; set; }
    /// <summary>Exploration endurance; refilled to the TrpgMaxEndurance config when the daily reset period moves on.</summary>
    [BsonElement("endurance")] public int Endurance { get; set; }
    [BsonElement("endurance_period")] public long EndurancePeriod { get; set; }
    /// <summary>Per-investigator talents and day-long buffs; the investigators themselves are derived from finished unlock targets.</summary>
    [BsonElement("roles")] public List<TrpgRoleState> Roles { get; set; } = new();
    /// <summary>Examines ever passed (the check results maze cards and functions read).</summary>
    [BsonElement("examines_passed")] public List<int> ExaminesPassed { get; set; } = new();
    /// <summary>The check in progress, if any.</summary>
    [BsonElement("examine")] public TrpgExamineState? Examine { get; set; }
    /// <summary>Monotonic counter that keeps repeatable reward claim keys unique.</summary>
    [BsonElement("reward_seq")] public long RewardSeq { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class TrpgRoleState
{
    [BsonElement("id")] public int Id { get; set; }
    [BsonElement("talents")] public List<int> Talents { get; set; } = new();
    [BsonElement("buffs")] public List<TrpgBuffState> Buffs { get; set; } = new();
}

/// <summary>A buff that lasts until the end of the daily reset period it was added in.</summary>
[BsonIgnoreExtraElements]
public sealed class TrpgBuffState
{
    [BsonElement("id")] public int Id { get; set; }
    [BsonElement("period")] public long Period { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class TrpgExamineState
{
    [BsonElement("examine")] public int ExamineId { get; set; }
    [BsonElement("action")] public int ActionId { get; set; }
    [BsonElement("round")] public int Round { get; set; }
    /// <summary>Score of each investigator in the current round (0 = not rolled yet).</summary>
    [BsonElement("scores")] public List<TrpgRoleScore> Scores { get; set; } = new();
    /// <summary>Score carried over from finished rounds.</summary>
    [BsonElement("carried")] public int Carried { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class TrpgRoleScore
{
    [BsonElement("role")] public int RoleId { get; set; }
    [BsonElement("score")] public int Score { get; set; }
}

public partial class Player
{
    [BsonElement("trpg")] public TrpgState Trpg { get; set; } = new();
}
