using AscNet.Common.MsgPack;

namespace AscNet.Common.Database;

public sealed partial class GuildWarPlayerRound
{
    public GuildWarFightCandidate? PendingFight { get; set; }
    public List<GuildWarFightRecord> FightRecords { get; set; } = [];
}

public sealed class GuildWarFightCandidate
{
    public long FightId { get; set; }
    public long Seed { get; set; }
    public int StageId { get; set; }
    public int Uid { get; set; }
    public int Type { get; set; }
    public int NodeId { get; set; }
    public int MonsterId { get; set; }
    public int GameThrough { get; set; }
    public int AliveType { get; set; }
    public int Energy { get; set; }
    public long CreatedAt { get; set; }
    public bool Settled { get; set; }
    public bool Confirmed { get; set; }
    public bool IsWin { get; set; }
    public int LeftTime { get; set; }
    public long BasePoint { get; set; }
    public GuildWarTeamInfo Team { get; set; } = new();
    public GuildWarFightResult Result { get; set; } = new();
    public byte[] PreFightResponse { get; set; } = [];
}
