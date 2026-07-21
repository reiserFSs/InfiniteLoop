using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.dormitory;
using AscNet.Table.V2.share.dormitory.furniture;
using MessagePack;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class ActiveDormItemRequest { public int DormitoryId { get; set; } }
[MessagePackObject(true)] public sealed class ActiveDormItemResponse { public int Code { get; set; } public int DormitoryId { get; set; } public string DormitoryName { get; set; } = string.Empty; public List<DormFurnitureData> FurnitureList { get; set; } = []; }
[MessagePackObject(true)] public sealed class DormRenameRequest { public int DormitoryId { get; set; } public string NewName { get; set; } = string.Empty; }
[MessagePackObject(true)] public sealed class DormRenameResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class DormFurnitureData { public int Id { get; set; } public uint ConfigId { get; set; } public int X { get; set; } public int Y { get; set; } public int Angle { get; set; } public int DormitoryId { get; set; } public int Addition { get; set; } public List<int> AttrList { get; set; } = []; public List<int> BaseAttrList { get; set; } = []; public bool IsLocked { get; set; } }
[MessagePackObject(true)] public sealed class PutFurnitureData { public int Id { get; set; } public int X { get; set; } public int Y { get; set; } public int Angle { get; set; } }
[MessagePackObject(true)] public sealed class PutFurnitureRequest { public int DormitoryId { get; set; } public List<PutFurnitureData> FurnitureList { get; set; } = []; }
[MessagePackObject(true)] public sealed class PutFurnitureResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class DormLayoutFurnitureData { public uint ConfigId { get; set; } public int X { get; set; } public int Y { get; set; } public int Angle { get; set; } }
[MessagePackObject(true)] public sealed class DormSnapshotLayoutRequest { public List<DormLayoutFurnitureData> FurnitureList { get; set; } = []; }
[MessagePackObject(true)] public sealed class DormSnapshotLayoutResponse { public int Code { get; set; } public string ShareId { get; set; } = string.Empty; public int SnapshotTimes { get; set; } }
[MessagePackObject(true)] public sealed class DormGetPlayerLayoutRequest { public string ShareId { get; set; } = string.Empty; }
[MessagePackObject(true)] public sealed class DormGetPlayerLayoutResponse { public int Code { get; set; } public List<DormLayoutFurnitureData> FurnitureList { get; set; } = []; }
[MessagePackObject(true)] public sealed class DormCollectLayoutRequest { public int LayoutId { get; set; } public string LayoutName { get; set; } = string.Empty; public List<DormLayoutFurnitureData> FurnitureList { get; set; } = []; }
[MessagePackObject(true)] public sealed class DormCollectLayoutResponse { public int Code { get; set; } public uint CreateTime { get; set; } public int NewLayoutId { get; set; } }
[MessagePackObject(true)] public sealed class DormBindLayoutRequest { public int DormitoryId { get; set; } public int LayoutId { get; set; } }
[MessagePackObject(true)] public sealed class DormBindLayoutResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class DormUnBindLayoutRequest { public int DormitoryId { get; set; } }
[MessagePackObject(true)] public sealed class DormUnBindLayoutResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class DormRecommendRequest { }
[MessagePackObject(true)] public sealed class DormRecommendResponse { public int Code { get; set; } public List<long> PlayerIds { get; set; } = []; }
[MessagePackObject(true)] public sealed class DormDetailsRequest { public List<long> Players { get; set; } = []; }
[MessagePackObject(true)] public sealed class DormDetail { public long PlayerId { get; set; } public string PlayerName { get; set; } = string.Empty; public int PlayerHead { get; set; } public int PlayerHeadFrame { get; set; } public int DormitoryId { get; set; } public string DormitoryName { get; set; } = string.Empty; public int DormitoryType { get; set; } = 1; public int FurnitureScore { get; set; } public int FurnitureCount { get; set; } public bool IsOnline { get; set; } public List<int> DormitoryAttr { get; set; } = []; }
[MessagePackObject(true)] public sealed class DormDetailsResponse { public int Code { get; set; } public List<DormDetail> Details { get; set; } = []; }
[MessagePackObject(true)] public sealed class DormVisitRequest { public long TargetId { get; set; } public int DormitoryId { get; set; } public uint CharacterId { get; set; } }
[MessagePackObject(true)] public sealed class DormVisitResponse { public int Code { get; set; } public string PlayerName { get; set; } = string.Empty; public List<DormFurnitureData> FurnitureList { get; set; } = []; public List<NotifyDormitoryData.NotifyDormitoryDataDormitory> DormitoryList { get; set; } = []; public List<object> CharacterList { get; set; } = []; public List<object> VisitorList { get; set; } = []; }

internal partial class DormModule
{
    [RequestPacketHandler("ActiveDormItemRequest")]
    public static void ActiveDormItemRequestHandler(Session session, Packet.Request packet)
    {
        ActiveDormItemRequest request = packet.Deserialize<ActiveDormItemRequest>();
        DormitoryTable? room = TableReaderV2.Parse<DormitoryTable>().FirstOrDefault(x => x.Id == request.DormitoryId);
        if (room is null || session.player.Dorm.Rooms.Any(x => x.Id == request.DormitoryId)) { session.SendResponse(new ActiveDormItemResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        int itemId = room.ConsumeItemId ?? 0, count = room.ConsumeItemCount ?? 0;
        Item? item = itemId > 0 && count > 0 ? session.inventory.Items.FirstOrDefault(x => x.Id == itemId) : null;
        if (itemId > 0 && count > 0 && (item is null || item.Count < count)) { session.SendResponse(new ActiveDormItemResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        if (item is not null) session.inventory.Do(itemId, -count);
        PlayerDormRoom saved = new() { Id = (uint)room.Id, Name = room.InitName ?? string.Empty };
        List<PlayerDormFurniture> furniture = InitialFurniture(room, session.player.Dorm);
        session.player.Dorm.Rooms.Add(saved); session.player.Dorm.Furniture.AddRange(furniture);
        foreach (uint id in furniture.Select(x => x.ConfigId).Distinct()) if (!session.player.Dorm.FurnitureUnlocks.Contains(id)) session.player.Dorm.FurnitureUnlocks.Add(id);
        if (item is not null) session.inventory.Save();
        session.player.Save();
        if (item is not null) session.SendPush(new NotifyItemDataList { ItemDataList = [session.inventory.Items.First(x => x.Id == itemId)] });
        session.SendResponse(new ActiveDormItemResponse { DormitoryId = room.Id, DormitoryName = saved.Name, FurnitureList = furniture.Select(RoomFurniture).ToList() }, packet.Id);
    }

    [RequestPacketHandler("DormRenameRequest")]
    public static void DormRenameRequestHandler(Session session, Packet.Request packet)
    {
        DormRenameRequest request = packet.Deserialize<DormRenameRequest>();
        int max = DormConfig().GetValueOrDefault("DormReNameLen");
        PlayerDormRoom? room = session.player.Dorm.Rooms.FirstOrDefault(x => x.Id == request.DormitoryId);
        if (room is null || string.IsNullOrWhiteSpace(request.NewName) || request.NewName.Length > max) { session.SendResponse(new DormRenameResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        room.Name = request.NewName.Trim(); session.player.Save(); session.SendResponse(new DormRenameResponse(), packet.Id);
    }

    [RequestPacketHandler("PutFurnitureRequest")]
    public static void PutFurnitureRequestHandler(Session session, Packet.Request packet)
    {
        PutFurnitureRequest request = packet.Deserialize<PutFurnitureRequest>();
        DormitoryTable? room = TableReaderV2.Parse<DormitoryTable>().FirstOrDefault(x => x.Id == request.DormitoryId);
        Dictionary<uint, FurnitureTable> tables = TableReaderV2.Parse<FurnitureTable>().ToDictionary(x => (uint)x.Id);
        bool valid = room is not null && session.player.Dorm.Rooms.Any(x => x.Id == request.DormitoryId)
            && request.FurnitureList.Count <= DormConfig().GetValueOrDefault("DormMaxPutFurnitureCount")
            && request.FurnitureList.Select(x => x.Id).Distinct().Count() == request.FurnitureList.Count
            && request.FurnitureList.All(x => session.player.Dorm.Furniture.FirstOrDefault(y => y.Id == x.Id) is { } owned
                && tables.ContainsKey(owned.ConfigId) && x.X >= 0 && x.X < room.Width && x.Y >= 0 && x.Y < room.Height && x.Angle is >= 0 and <= 7);
        if (!valid) { session.SendResponse(new PutFurnitureResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        uint now = Now(); SettleRecovery(session, now);
        foreach (PutFurnitureData placed in request.FurnitureList)
        { PlayerDormFurniture furniture = session.player.Dorm.Furniture.First(x => x.Id == placed.Id); furniture.X = placed.X; furniture.Y = placed.Y; furniture.Angle = placed.Angle; furniture.DormitoryId = request.DormitoryId; }
        HashSet<int> placedIds = request.FurnitureList.Select(x => x.Id).ToHashSet();
        foreach (PlayerDormFurniture furniture in session.player.Dorm.Furniture.Where(x => x.DormitoryId == request.DormitoryId && !placedIds.Contains(x.Id))) furniture.DormitoryId = -1;
        RecalculateRecovery(session);
        foreach (PlayerDormCharacter character in session.player.Dorm.Characters.Where(x => x.DormitoryId == request.DormitoryId)) character.LastRecoveryTime = now;
        session.player.Save(); SendCharacterRecovery(session); session.SendResponse(new PutFurnitureResponse(), packet.Id);
    }

    [RequestPacketHandler("DormSnapshotLayoutRequest")]
    public static void DormSnapshotLayoutRequestHandler(Session session, Packet.Request packet)
    {
        DormSnapshotLayoutRequest request = packet.Deserialize<DormSnapshotLayoutRequest>(); uint now = Now();
        if (session.player.Dorm.LastSnapshotTime / 86_400 != now / 86_400) session.player.Dorm.SnapshotTimes = 0;
        bool valid = ValidLayout(request.FurnitureList) && session.player.Dorm.SnapshotTimes < DormConfig().GetValueOrDefault("DormMaxSnapshotTimes");
        if (!valid) { session.SendResponse(new DormSnapshotLayoutResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        string shareId = Guid.NewGuid().ToString("N"); session.player.Dorm.Shares.Add(new PlayerDormShare { ShareId = shareId, CreateTime = now, FurnitureList = Layout(request.FurnitureList) }); session.player.Dorm.SnapshotTimes++; session.player.Dorm.LastSnapshotTime = now; session.player.Save();
        session.SendResponse(new DormSnapshotLayoutResponse { ShareId = shareId, SnapshotTimes = session.player.Dorm.SnapshotTimes }, packet.Id);
    }

    [RequestPacketHandler("DormGetPlayerLayoutRequest")]
    public static void DormGetPlayerLayoutRequestHandler(Session session, Packet.Request packet)
    {
        DormGetPlayerLayoutRequest request = packet.Deserialize<DormGetPlayerLayoutRequest>();
        PlayerDormShare? share = Player.collection.Find(x => x.Dorm.Shares.Any(s => s.ShareId == request.ShareId)).Limit(1).FirstOrDefault()?.Dorm.Shares.FirstOrDefault(x => x.ShareId == request.ShareId);
        session.SendResponse(share is null ? new DormGetPlayerLayoutResponse { Code = DormRequestDataInvalid } : new DormGetPlayerLayoutResponse { FurnitureList = Layout(share.FurnitureList) }, packet.Id);
    }

    [RequestPacketHandler("DormCollectLayoutRequest")]
    public static void DormCollectLayoutRequestHandler(Session session, Packet.Request packet)
    {
        DormCollectLayoutRequest request = packet.Deserialize<DormCollectLayoutRequest>();
        PlayerDormLayout? layout = session.player.Dorm.Layouts.FirstOrDefault(x => x.LayoutId == request.LayoutId);
        int max = DormConfig().GetValueOrDefault("DormLayoutMaxCount");
        if ((layout is null && session.player.Dorm.Layouts.Count >= max) || string.IsNullOrWhiteSpace(request.LayoutName) || !ValidLayout(request.FurnitureList)) { session.SendResponse(new DormCollectLayoutResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        uint now = Now();
        if (layout is null) { layout = new PlayerDormLayout { LayoutId = session.player.Dorm.NextLayoutId++, CreateTime = now }; session.player.Dorm.Layouts.Add(layout); }
        layout.Name = request.LayoutName.Trim(); layout.FurnitureList = Layout(request.FurnitureList); session.player.Save();
        session.SendResponse(new DormCollectLayoutResponse { CreateTime = layout.CreateTime, NewLayoutId = layout.LayoutId }, packet.Id);
    }

    [RequestPacketHandler("DormBindLayoutRequest")]
    public static void DormBindLayoutRequestHandler(Session session, Packet.Request packet)
    {
        DormBindLayoutRequest request = packet.Deserialize<DormBindLayoutRequest>();
        if (!session.player.Dorm.Rooms.Any(x => x.Id == request.DormitoryId) || !session.player.Dorm.Layouts.Any(x => x.LayoutId == request.LayoutId)) { session.SendResponse(new DormBindLayoutResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        session.player.Dorm.BindRelations.RemoveAll(x => x.DormitoryId == request.DormitoryId); session.player.Dorm.BindRelations.Add(new PlayerDormBindRelation { DormitoryId = request.DormitoryId, LayoutId = request.LayoutId }); session.player.Save(); session.SendResponse(new DormBindLayoutResponse(), packet.Id);
    }

    [RequestPacketHandler("DormUnBindLayoutRequest")]
    public static void DormUnBindLayoutRequestHandler(Session session, Packet.Request packet)
    { DormUnBindLayoutRequest request = packet.Deserialize<DormUnBindLayoutRequest>(); if (session.player.Dorm.BindRelations.RemoveAll(x => x.DormitoryId == request.DormitoryId) == 0) { session.SendResponse(new DormUnBindLayoutResponse { Code = DormRequestDataInvalid }, packet.Id); return; } session.player.Save(); session.SendResponse(new DormUnBindLayoutResponse(), packet.Id); }

    [RequestPacketHandler("DormRecommendRequest")]
    public static void DormRecommendRequestHandler(Session session, Packet.Request packet)
    {
        uint now = Now(); Dictionary<string, int> config = DormConfig();
        if (now < session.player.Dorm.LastRecommendTime + config.GetValueOrDefault("DormRecommendCd")) { session.SendResponse(new DormRecommendResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        int timeout = config.GetValueOrDefault("DormVisitTimeoutInterval");
        session.player.Dorm.Visits.RemoveAll(x => x.VisitTime > now || now - x.VisitTime >= timeout);
        int limit = config.GetValueOrDefault("DormRecommendCountLimit"); HashSet<long> visited = session.player.Dorm.Visits.Select(x => x.PlayerId).ToHashSet();
        List<long> players = Player.collection.Find(x => x.PlayerData.Id != session.player.PlayerData.Id && x.Dorm.Rooms.Count > 0).SortBy(x => x.PlayerData.Id).Limit(config.GetValueOrDefault("DormRecommendedCacheMaxCount")).ToList().Select(x => x.PlayerData.Id).Where(x => !visited.Contains(x)).Take(limit).ToList();
        session.player.Dorm.LastRecommendTime = now; session.player.Save(); session.SendResponse(new DormRecommendResponse { PlayerIds = players }, packet.Id);
    }

    [RequestPacketHandler("DormDetailsRequest")]
    public static void DormDetailsRequestHandler(Session session, Packet.Request packet)
    {
        DormDetailsRequest request = packet.Deserialize<DormDetailsRequest>(); int limit = DormConfig().GetValueOrDefault("DormRecommendPlayerCountLimit");
        if (request.Players.Count == 0 || request.Players.Count > limit || request.Players.Distinct().Count() != request.Players.Count) { session.SendResponse(new DormDetailsResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        List<DormDetail> details = request.Players.Select(Player.TryFromPlayerId).Where(x => x is not null).Cast<Player>().Select(Detail).ToList(); session.SendResponse(new DormDetailsResponse { Details = details }, packet.Id);
    }

    [RequestPacketHandler("DormVisitRequest")]
    public static void DormVisitRequestHandler(Session session, Packet.Request packet)
    {
        DormVisitRequest request = packet.Deserialize<DormVisitRequest>(); Player? target = request.TargetId == session.player.PlayerData.Id ? session.player : Player.TryFromPlayerId(request.TargetId);
        PlayerDormRoom? room = target?.Dorm.Rooms.FirstOrDefault(x => x.Id == request.DormitoryId);
        PlayerDormCharacter? visitor = request.CharacterId == 0 ? null : session.player.Dorm.Characters.FirstOrDefault(x => x.CharacterId == request.CharacterId);
        if (target is null || room is null || (request.CharacterId != 0 && visitor is null)) { session.SendResponse(new DormVisitResponse { Code = DormRequestDataInvalid }, packet.Id); return; }
        uint now = Now(); int max = DormConfig().GetValueOrDefault("DormVisitRecordNum"); session.player.Dorm.Visits.RemoveAll(x => x.PlayerId == request.TargetId); session.player.Dorm.Visits.Insert(0, new PlayerDormVisit { PlayerId = request.TargetId, VisitTime = now }); if (session.player.Dorm.Visits.Count > max) session.player.Dorm.Visits.RemoveRange(max, session.player.Dorm.Visits.Count - max); session.player.Save();
        session.SendResponse(new DormVisitResponse { PlayerName = target.PlayerData.Name, FurnitureList = target.Dorm.Furniture.Where(x => x.DormitoryId == request.DormitoryId).Select(RoomFurniture).ToList(), DormitoryList = target.Dorm.Rooms.Select(x => new NotifyDormitoryData.NotifyDormitoryDataDormitory { DormitoryId = x.Id, DormitoryName = x.Name }).ToList(), CharacterList = target.Dorm.Characters.Where(x => x.DormitoryId == request.DormitoryId).Select(x => (object)VisitCharacter(x, x.DormitoryId)).ToList(), VisitorList = visitor is null ? [] : [(object)VisitCharacter(visitor, request.DormitoryId)] }, packet.Id);
    }

    private static uint Now() => checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private static Dictionary<string, int> DormConfig() => TableReaderV2.Parse<ConfigTable>().Where(x => x.Key.StartsWith("Dorm", StringComparison.Ordinal)).ToDictionary(x => x.Key, x => int.TryParse(x.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0);
    private static List<PlayerDormFurniture> InitialFurniture(DormitoryTable room, PlayerDormState state)
    { Dictionary<uint, FurnitureTable> furniture = TableReaderV2.Parse<FurnitureTable>().ToDictionary(x => (uint)x.Id); FurnitureExtraAttrTable? initial = TableReaderV2.Parse<FurnitureExtraAttrTable>().FirstOrDefault(x => x.Id == DormConfig().GetValueOrDefault("DormInitFurnitureAttr")); Dictionary<int, int> baseAttr = TableReaderV2.Parse<FurnitureBaseAttrTable>().ToDictionary(x => x.Id, x => x.Value); List<int> bases = initial?.AttrIds.Take(3).Concat(Enumerable.Repeat(0, 3)).Take(3).ToList() ?? []; int total = initial is null ? 0 : baseAttr.GetValueOrDefault(initial.BaseAttrId); return room.InitFurniture.Zip(room.InitFurniturePos, (id, pos) => (id, pos)).Where(x => x.id > 0 && furniture.ContainsKey((uint)x.id)).Select(x => { int[] p = x.pos.Split('|').Select(int.Parse).ToArray(); return new PlayerDormFurniture { Id = state.NextFurnitureId++, ConfigId = (uint)x.id, X = p[0], Y = p[1], Angle = p[2], DormitoryId = room.Id, AttrList = Split(total, bases).ToList(), BaseAttrList = bases.ToList() }; }).ToList(); }
    private static DormFurnitureData RoomFurniture(PlayerDormFurniture x) => new() { Id = x.Id, ConfigId = x.ConfigId, X = x.X, Y = x.Y, Angle = x.Angle, DormitoryId = x.DormitoryId, Addition = x.Addition, AttrList = x.AttrList, BaseAttrList = x.BaseAttrList, IsLocked = x.IsLocked };
    private static List<PlayerDormLayoutFurniture> Layout(IEnumerable<DormLayoutFurnitureData> values) => values.Select(x => new PlayerDormLayoutFurniture { ConfigId = x.ConfigId, X = x.X, Y = x.Y, Angle = x.Angle }).ToList();
    private static List<DormLayoutFurnitureData> Layout(IEnumerable<PlayerDormLayoutFurniture> values) => values.Select(x => new DormLayoutFurnitureData { ConfigId = x.ConfigId, X = x.X, Y = x.Y, Angle = x.Angle }).ToList();
    private static bool ValidLayout(IEnumerable<DormLayoutFurnitureData> values) { List<DormLayoutFurnitureData> list = values.ToList(); return list.Count > 0 && list.Count <= DormConfig().GetValueOrDefault("DormMaxPutFurnitureCount") && list.All(x => x.ConfigId > 0 && x.X >= 0 && x.Y >= 0 && x.Angle is >= 0 and <= 7); }
    private static NotifyDormitoryData.NotifyDormitoryDataCharacter VisitCharacter(PlayerDormCharacter character, int dormitoryId) => new() { CharacterId = character.CharacterId, DormitoryId = dormitoryId, Mood = character.Mood / 100, Vitality = character.Vitality / 100, MoodSpeed = character.MoodSpeed, VitalitySpeed = character.VitalitySpeed, LastFondleRecoveryTime = character.LastFondleRecoveryTime, LeftFondleCount = character.LeftFondleCount, EventList = character.EventList.Select(evt => new NotifyDormitoryData.NotifyDormitoryDataEvent { EventId = evt.EventId, EndTime = evt.EndTime }).ToList() };
    private static DormDetail Detail(Player player) { Dictionary<int, FurnitureAdditionalAttrTable> additions = FurnitureData.Value.AdditionalAttrs.ToDictionary(x => x.AttributeId); (PlayerDormRoom Room, List<PlayerDormFurniture> Furniture) selected = player.Dorm.Rooms.Select(room => (Room: room, Furniture: player.Dorm.Furniture.Where(x => x.DormitoryId == room.Id).ToList())).OrderByDescending(x => x.Furniture.Sum(Score)).ThenBy(x => x.Room.Id).FirstOrDefault(); PlayerDormRoom? room = selected.Room; List<PlayerDormFurniture> furniture = selected.Furniture ?? []; List<int> attrs = Enumerable.Range(0, 3).Select(i => furniture.Sum(x => x.AttrList.ElementAtOrDefault(i) + (additions.GetValueOrDefault(x.Addition)?.AddType == 2 ? additions.GetValueOrDefault(x.Addition)?.AddValue.ElementAtOrDefault(i) ?? 0 : 0))).ToList(); return new DormDetail { PlayerId = player.PlayerData.Id, PlayerName = player.PlayerData.Name, PlayerHead = checked((int)player.PlayerData.CurrHeadPortraitId), PlayerHeadFrame = checked((int)player.PlayerData.CurrHeadFrameId), DormitoryId = (int)(room?.Id ?? 0), DormitoryName = room?.Name ?? string.Empty, FurnitureCount = furniture.Count, FurnitureScore = furniture.Sum(Score), DormitoryAttr = attrs }; }
}
