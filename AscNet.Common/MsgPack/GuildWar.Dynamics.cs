namespace AscNet.Common.MsgPack;

[global::MessagePack.MessagePackObject(true)]
public sealed class GuildWarReinforcementData
{
    public int Uid { get; set; }
    public int ReinforcementId { get; set; }
    public long CurHp { get; set; }
    public long HpMax { get; set; }
    public int CurNodeId { get; set; }
    public long DeadTime { get; set; }
    public long NextMoveTime { get; set; }
    public long ReadyDoneTime { get; set; }
    public List<long> SupportedPlayerIds { get; set; } = [];
}
[global::MessagePack.MessagePackObject(true)]
public sealed class GuildWarMyStationedData
{
    public int NodeId { get; set; }
    public int CharacterId { get; set; }
}
[global::MessagePack.MessagePackObject(true)]
public sealed class GuildWarGuildStationedData
{
    public int NodeId { get; set; }
    public int Count { get; set; }
}
[global::MessagePack.MessagePackObject(true)]
public sealed class GuildWarSupportReinforcementRequest { public int ReinforcementUid { get; set; } }
[global::MessagePack.MessagePackObject(true)]
public sealed class GuildWarSupportReinforcementResponse : GuildResponse { public GuildWarReinforcementData ReinforcementData { get; set; } = new(); }
[global::MessagePack.MessagePackObject(true)]
public sealed class GuildWarCancelSupportReinforcementResponse : GuildResponse { public GuildWarReinforcementData ReinforcementData { get; set; } = new(); }
[global::MessagePack.MessagePackObject(true)]
public sealed class XGuildWarSelectDefenseNodeRequest { public int NodeId { get; set; } }
[global::MessagePack.MessagePackObject(true)]
public sealed class XGuildWarSelectDefenseNodeResponse : GuildResponse { }
[global::MessagePack.MessagePackObject(true)]
public sealed class XGuildWarBeStationedRequest { public int NodeUid { get; set; } public int CharacterId { get; set; } }
[global::MessagePack.MessagePackObject(true)]
public sealed class XGuildWarBeStationedResponse : GuildResponse
{
    public List<GuildWarMyStationedData> MyStationedData { get; set; } = [];
    public List<GuildWarGuildStationedData> GuildStationedData { get; set; } = [];
}
public partial class NotifyGuildWarActivityData
{
    public List<int> BeStationedFightRecord { get; set; } = [];
    public partial class NotifyGuildWarActivityDataActivityData
    {
        public partial class NotifyGuildWarActivityDataActivityDataRoundData
        {
            public int GameThrough { get; set; }
            public int BossDead { get; set; }
            public int DragonRage { get; set; }
            public int FullDragonRageTime { get; set; }
            public int GameThroughCfgId { get; set; }
            public int DragonRageCfgId { get; set; }
            public int FullDragonRageCfgId { get; set; }
            public long LastDragonRageReduceTime { get; set; }
            public int AttackTimes { get; set; }
            public long NextAttackTime { get; set; }
            public Dictionary<long,int> DefenseDic { get; set; } = [];
            public List<int> LastProtectNodeIds { get; set; } = [];
            public List<GuildWarReinforcementData> ReinforcementData { get; set; } = [];
            public List<GuildWarMyStationedData> MyStationedData { get; set; } = [];
            public List<GuildWarGuildStationedData> GuildStationedData { get; set; } = [];
            public partial class NotifyGuildWarActivityDataActivityDataRoundDataNodeData
            {
                public int NodeType { get; set; }
                public long NextReinforcementsBornTime { get; set; }
                public int LastReinforcementId { get; set; }
                public int LastAttackedReinforcementId { get; set; }
                public long LastReinforcementAttackedTime { get; set; }
                public long NextBossTreatMstTime { get; set; }
            }
        }
        public partial class NotifyGuildWarActivityDataActivityDataAction
        {
            public int ReinforcementUid { get; set; }
            public GuildWarReinforcementData? ReinforcementData { get; set; }
            public int PreNodeId { get; set; }
            public int NextNodeId { get; set; }
            public int DragonRageValue { get; set; }
            public int GameThroughId { get; set; }
            public bool IsRealTime { get; set; }
        }
    }
}
