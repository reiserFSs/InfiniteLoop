namespace AscNet.GameServer;

public sealed class PendingBossInshotFight
{
    public int ActivityId { get; init; }
    public int? TowerId { get; init; }
    public uint StageId { get; init; }
    public uint FightId { get; init; }
    public int CharacterConfigId { get; init; }
    public int CharacterId { get; init; }
    public bool IsTower { get; init; }
    public int? PersistedSettlementScore { get; set; }
    public bool PersistedSettlementWasNewRecord { get; set; }
}

public partial class Session
{
    public PendingBossInshotFight? PendingBossInshotFight { get; set; }
}
