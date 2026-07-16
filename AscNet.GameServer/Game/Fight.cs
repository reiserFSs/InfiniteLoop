using AscNet.Common.MsgPack;

namespace AscNet.GameServer.Game
{
    public class BossSinglePendingScore
    {
        public int StageId { get; init; }
        public int StageType { get; init; }
        public int SectionId { get; init; }
        public BossSingleFightResult Result { get; init; } = new();
        public List<int> Characters { get; init; } = new();
        public List<int> Partners { get; init; } = new();
    }

    public class Fight
    {
        public PreFightRequest PreFight { get; }
        public uint FightId { get; }
        public string Uuid { get; }

        public Fight(PreFightRequest preFight, uint fightId = 0, string? uuid = null)
        {
            PreFight = preFight;
            FightId = fightId;
            Uuid = uuid ?? string.Empty;
        }
    }
}
