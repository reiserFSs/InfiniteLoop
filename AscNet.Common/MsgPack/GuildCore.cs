using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public class GuildResponse { public int Code { get; set; } }

[MessagePackObject(true)]
public sealed class GuildEmptyRequest { }
