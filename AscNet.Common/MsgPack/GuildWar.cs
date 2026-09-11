using MessagePack;
using Activity = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData;
using Node = AscNet.Common.MsgPack.NotifyGuildWarActivityData.NotifyGuildWarActivityDataActivityData.NotifyGuildWarActivityDataActivityDataRoundData.NotifyGuildWarActivityDataActivityDataRoundDataNodeData;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class GuildWarGetActivityDataRequest { }
[MessagePackObject(true)]
public sealed class GuildWarGetActivityDataResponse : GuildResponse { public Activity ActivityData { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class GuildWarSelectDifficultyRequest { public int DifficultyId { get; set; } }
[MessagePackObject(true)]
public sealed class GuildWarSelectDifficultyResponse : GuildResponse { }
[MessagePackObject(true)]
public sealed class GuildWarEditLineRequest { public List<int> AttackPlan { get; set; } = []; }
[MessagePackObject(true)]
public sealed class GuildWarEditLineResponse : GuildResponse { }
[MessagePackObject(true)]
public sealed class GuildWarMoveRequest { public int CurNodeId { get; set; } public int NextNodeId { get; set; } }
[MessagePackObject(true)]
public sealed class GuildWarMoveResponse : GuildResponse { public List<Node> NodeDatas { get; set; } = []; }
[MessagePackObject(true)]
public sealed class GuildWarCanMoveRequest { public int CurNodeId { get; set; } public int NextNodeId { get; set; } }
[MessagePackObject(true)]
public sealed class GuildWarCanMoveResponse : GuildResponse { }
[MessagePackObject(true)]
public sealed class NotifyGuildWarActivityDataChange { public Activity ActivityData { get; set; } = new(); }
[MessagePackObject(true)]
public sealed class NotifyAddExtraActionPoint { public int ItemId { get; set; } public int Count { get; set; } }
