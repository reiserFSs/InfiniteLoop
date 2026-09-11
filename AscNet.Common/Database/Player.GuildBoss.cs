using AscNet.Common.MsgPack;

namespace AscNet.Common.Database;

public sealed partial class GuildPlayerState
{
    public GuildBossPlayerState Boss { get; set; } = new();
}

public sealed class GuildBossPlayerState
{
    public long GuildId { get; set; }
    public long Period { get; set; }
    public GuildBossFightStyle FightStyle { get; set; } = new();
    public GuildBossAttemptState? Attempt { get; set; }
}

public sealed class GuildBossAttemptState
{
    public long StageId { get; set; }
    public long GuildId { get; set; }
    public long Period { get; set; }
    public int CaptainPos { get; set; }
    public int FirstFightPos { get; set; }
    public long FightId { get; set; }
    public long Seed { get; set; }
    public long StartedAt { get; set; }
    public bool Settled { get; set; }
    public bool Won { get; set; }
    public bool Uploaded { get; set; }
    public List<int> CardIds { get; set; } = [];
    public List<int> RobotIds { get; set; } = [];
    public List<CharacterHeadInfo> CharacterHeadInfoList { get; set; } = [];
    public byte[] PreFightBytes { get; set; } = [];
    public byte[] SettleData { get; set; } = [];
    public byte[] SettleRequest { get; set; } = [];
    public GuildBossFightResult Result { get; set; } = new();
    public long SubHp { get; set; }
    public int Contribute { get; set; }
}
