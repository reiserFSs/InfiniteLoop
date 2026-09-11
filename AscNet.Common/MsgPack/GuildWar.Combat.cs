using MessagePack;

namespace AscNet.Common.MsgPack;

public partial class PreFightRequest
{
    public partial class PreFightRequestPreFightData
    {
        public int GuildWarUid { get; set; }
    }
}

[MessagePackObject(true)]
public sealed class GuildWarFightResult
{
    public int Type { get; set; }
    public int NodeId { get; set; }
    public int MonsterId { get; set; }
    public long CurHp { get; set; }
    public long Damage { get; set; }
    public long MaxDamage { get; set; }
    public long Point { get; set; }
    public int IsNewRecord { get; set; }
    public int costCount { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildWarFightRecord
{
    public int Uid { get; set; }
    [IgnoreMember]
    public int Type { get; set; }
    public int AliveType { get; set; }
    public int GameThrough { get; set; }
    public long MaxDamage { get; set; }
    [IgnoreMember]
    public long MaxPoint { get; set; }
}

[MessagePackObject(true)] public sealed class GuildWarConfirmFightResultRequest { public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class GuildWarConfirmFightResultResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildWarSweepRequest { public int Uid { get; set; } public int SweepType { get; set; } public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class GuildWarSweepResponse : GuildResponse { }
