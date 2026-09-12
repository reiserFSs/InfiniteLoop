using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class NameplateData
{
    public int Id { get; set; }
    public int Exp { get; set; }
    public long EndTime { get; set; }
    public long GetTime { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyNameplateInfo
{
    public NameplateData Nameplate { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class WearNameplateRequest
{
    public int NameplateId { get; set; }
}

[MessagePackObject(true)]
public sealed class WearNameplateResponse
{
    public int Code { get; set; }
}
