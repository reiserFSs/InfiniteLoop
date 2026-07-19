using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.dormitory;
using AscNet.Table.V2.share.dormitory.character;
using MessagePack;

namespace AscNet.GameServer.Handlers;

#pragma warning disable CS8618
[MessagePackObject(true)]
public class DormEnterRequest { }

[MessagePackObject(true)]
public class DormCharacterEvent
{
    public uint CharacterId;
    public List<DormEvent> EventList = [];
}

[MessagePackObject(true)]
public class DormEvent
{
    public int EventId;
    public uint EndTime;
}

[MessagePackObject(true)]
public class DormEnterResponse
{
    public int Code;
    public List<DormCharacterEvent> CharacterEvents = [];
}

[MessagePackObject(true)]
public class DormitoryListRequest { }

[MessagePackObject(true)]
public class DormitoryListResponse { public int Code; }

[MessagePackObject(true)]
public class DormWork
{
    public uint CharacterId;
    public int WorkPos;
}

[MessagePackObject(true)]
public class DormWorkData
{
    public uint CharacterId;
    public int WorkPos;
    public uint WorkEndTime;
    public int DormitoryNum;
    public int RewardNum;
    public int ResetCount;
}

[MessagePackObject(true)]
public class DormWorkRequest { public List<DormWork> Works = []; }

[MessagePackObject(true)]
public class DormWorkResponse
{
    public int Code;
    public List<DormWorkData> WorkList = [];
}

[MessagePackObject(true)]
public class DormWorkRewardRequest { public List<int> PosList = []; }

[MessagePackObject(true)]
public class DormWorkReward
{
    public int WorkPos;
    public int ItemId;
    public int ItemNum;
    public int ResetCount;
}

[MessagePackObject(true)]
public class DormWorkRewardResponse
{
    public int Code;
    public List<DormWorkReward> WorkRewards = [];
}

[MessagePackObject(true)]
public class NotifyCharacterVitality
{
    public uint CharacterId;
    public int Vitality;
}

[MessagePackObject(true)]
public class DormCharacterFinishAllEventRequest { }

[MessagePackObject(true)]
public class DormCharacterFinishAllEventResponse
{
    public int Code;
    public Dictionary<uint, int> MoodChange = [];
    public List<RewardGoods> RewardGoods = [];
}
#pragma warning restore CS8618

internal class DormModule
{
    private const int DormRequestDataInvalid = 20060040;

    public static NotifyDormitoryData BuildLoginData(Session session)
    {
        List<DormitoryTable> rooms = TableReaderV2.Parse<DormitoryTable>();
        Dictionary<string, int> config = Config();
        bool changed = false;
        foreach (DormitoryTable room in rooms.Where(room => room.IsFree == 1))
        {
            if (session.player.Dorm.Rooms.All(saved => saved.Id != (uint)room.Id))
            {
                session.player.Dorm.Rooms.Add(new PlayerDormRoom { Id = (uint)room.Id, Name = room.InitName ?? string.Empty });
                changed = true;
            }
        }

        foreach (uint characterId in session.character.Characters.Select(character => character.Id).Distinct())
        {
            if (session.player.Dorm.Characters.All(saved => saved.CharacterId != characterId))
            {
                session.player.Dorm.Characters.Add(new PlayerDormCharacter
                {
                    CharacterId = characterId,
                    Mood = config.GetValueOrDefault("DormMoodInitValue"),
                    Vitality = config.GetValueOrDefault("DormVitalityInitValue")
                });
                changed = true;
            }
        }

        if (changed)
            session.player.Save();

        return new NotifyDormitoryData
        {
            WorkList = session.player.Dorm.WorkList.Select(Work).ToList(),
            FurnitureUnlockList = session.player.Dorm.Furniture.Select(furniture => furniture.ConfigId).Distinct().ToList(),
            SnapshotTimes = session.player.Dorm.SnapshotTimes,
            DormitoryList = session.player.Dorm.Rooms.Select(room => new NotifyDormitoryData.NotifyDormitoryDataDormitory
            {
                DormitoryId = room.Id,
                DormitoryName = room.Name
            }).ToList(),
            FurnitureList = session.player.Dorm.Furniture.Select(furniture => new NotifyDormitoryData.NotifyDormitoryDataFurniture
            {
                Id = furniture.Id, ConfigId = furniture.ConfigId, X = furniture.X, Y = furniture.Y, Angle = furniture.Angle,
                DormitoryId = furniture.DormitoryId, Addition = furniture.Addition, AttrList = furniture.AttrList,
                BaseAttrList = furniture.BaseAttrList, IsLocked = furniture.IsLocked
            }).ToList(),
            CharacterList = session.player.Dorm.Characters.Select(character => new NotifyDormitoryData.NotifyDormitoryDataCharacter
            {
                CharacterId = character.CharacterId, DormitoryId = character.DormitoryId, Mood = character.Mood / 100,
                Vitality = character.Vitality / 100, MoodSpeed = character.MoodSpeed, VitalitySpeed = character.VitalitySpeed,
                LastFondleRecoveryTime = character.LastFondleRecoveryTime, LeftFondleCount = character.LeftFondleCount,
                EventList = character.EventList.Select(evt => new NotifyDormitoryData.NotifyDormitoryDataEvent { EventId = evt.EventId, EndTime = evt.EndTime }).ToList()
            }).ToList()
        };
    }

    [RequestPacketHandler("DormEnterRequest")]
    public static void DormEnterRequestHandler(Session session, Packet.Request packet)
    {
        session.SendResponse(new DormEnterResponse
        {
            CharacterEvents = session.player.Dorm.Characters.Select(character => new DormCharacterEvent
            {
                CharacterId = character.CharacterId,
                EventList = character.EventList.Select(evt => new DormEvent { EventId = evt.EventId, EndTime = evt.EndTime }).ToList()
            }).ToList()
        }, packet.Id);
        TaskModule.RecordConditionType(session, 29014);
    }

    [RequestPacketHandler("DormWorkRequest")]
    public static void DormWorkRequestHandler(Session session, Packet.Request packet)
    {
        DormWorkRequest request = packet.Deserialize<DormWorkRequest>();
        Dictionary<string, int> config = Config();
        List<DormCharacterWorkTable> table = TableReaderV2.Parse<DormCharacterWorkTable>();
        int unlocked = session.player.Dorm.Rooms.Count;
        DormCharacterWorkTable? work = table.Where(row => row.DormitoryNum <= unlocked).OrderByDescending(row => row.DormitoryNum).FirstOrDefault();
        HashSet<uint> owned = session.character.Characters.Select(character => character.Id).ToHashSet();
        bool valid = request.Works.Count > 0 && work is not null
            && request.Works.Select(entry => entry.CharacterId).Distinct().Count() == request.Works.Count
            && request.Works.Select(entry => entry.WorkPos).Distinct().Count() == request.Works.Count
            && request.Works.All(entry => entry.WorkPos > 0 && entry.WorkPos <= work!.Seat && owned.Contains(entry.CharacterId))
            && request.Works.All(entry => session.player.Dorm.WorkList.Where(active => active.WorkEndTime > 0).All(active => active.WorkPos != entry.WorkPos && active.CharacterId != entry.CharacterId))
            && request.Works.All(entry => session.player.Dorm.Characters.FirstOrDefault(character => character.CharacterId == entry.CharacterId) is { } character
                && character.Mood >= work!.Mood && character.Vitality >= work.Vitality);
        if (!valid)
        {
            session.SendResponse(new DormWorkResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int timeMultiplier = config.GetValueOrDefault("DormWorkTimePerVitality");
        List<PlayerDormWork> added = [];
        foreach (DormWork entry in request.Works)
        {
            PlayerDormCharacter character = session.player.Dorm.Characters.First(character => character.CharacterId == entry.CharacterId);
            int count = character.Vitality / work!.Vitality;
            character.Vitality -= count * work.Vitality;
            added.Add(new PlayerDormWork
            {
                CharacterId = entry.CharacterId, WorkPos = entry.WorkPos,
                WorkEndTime = checked(now + (uint)(count * work.Time * timeMultiplier)),
                DormitoryNum = work.DormitoryNum, RewardNum = count
            });
        }
        session.player.Dorm.WorkList.RemoveAll(saved => saved.WorkEndTime == 0
            && added.Any(entry => entry.WorkPos == saved.WorkPos || entry.CharacterId == saved.CharacterId));
        session.player.Dorm.WorkList.AddRange(added);
        session.player.Save();
        foreach (PlayerDormWork entry in added)
            session.SendPush(new NotifyCharacterVitality { CharacterId = entry.CharacterId, Vitality = session.player.Dorm.Characters.First(character => character.CharacterId == entry.CharacterId).Vitality / 100 });
        session.SendResponse(new DormWorkResponse { WorkList = added.Select(ResponseWork).ToList() }, packet.Id);
    }

    [RequestPacketHandler("DormWorkRewardRequest")]
    public static void DormWorkRewardRequestHandler(Session session, Packet.Request packet)
    {
        DormWorkRewardRequest request = packet.Deserialize<DormWorkRewardRequest>();
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Dictionary<int, DormCharacterWorkTable> workByRoom = TableReaderV2.Parse<DormCharacterWorkTable>().ToDictionary(row => row.DormitoryNum);
        bool valid = request.PosList.Count > 0 && request.PosList.Distinct().Count() == request.PosList.Count
            && request.PosList.All(pos => session.player.Dorm.WorkList.FirstOrDefault(work => work.WorkPos == pos) is { } work
                && work.WorkEndTime > 0 && work.WorkEndTime <= now && workByRoom.ContainsKey(work.DormitoryNum));
        if (!valid)
        {
            session.SendResponse(new DormWorkRewardResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        int multiplier = Config().GetValueOrDefault("DormWorkRewardPerVitality");
        List<DormWorkReward> rewards = request.PosList.Select(position =>
        {
            PlayerDormWork work = session.player.Dorm.WorkList.First(entry => entry.WorkPos == position);
            DormCharacterWorkTable row = workByRoom[work.DormitoryNum];
            int quantity = checked(work.RewardNum * multiplier);
            work.WorkEndTime = 0;
            return new DormWorkReward { WorkPos = position, ItemId = row.ItemId, ItemNum = quantity, ResetCount = work.ResetCount };
        }).ToList();
        List<Item> changedItems = rewards
            .GroupBy(reward => reward.ItemId)
            .Select(group => session.inventory.Do(group.Key, group.Sum(reward => reward.ItemNum)))
            .ToList();
        session.inventory.Save();
        session.player.Save();
        session.SendPush(new NotifyItemDataList { ItemDataList = changedItems });
        session.SendResponse(new DormWorkRewardResponse { WorkRewards = rewards }, packet.Id);
    }

    [RequestPacketHandler("DormCharacterFinishAllEventRequest")]
    public static void DormCharacterFinishAllEventRequestHandler(Session session, Packet.Request packet)
    {
        // Event reward and mood derivation are not authoritative yet; preserve nonempty events rather than lose rewards.
        int code = session.player.Dorm.Characters.Any(character => character.EventList.Count > 0) ? DormRequestDataInvalid : 0;
        session.SendResponse(new DormCharacterFinishAllEventResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler("DormitoryListRequest")]
    public static void DormitoryListRequestHandler(Session session, Packet.Request packet) => session.SendResponse(new DormitoryListResponse(), packet.Id);

    private static Dictionary<string, int> Config() => TableReaderV2.Parse<ConfigTable>()
        .Where(row => row.Key is "DormMoodInitValue" or "DormVitalityInitValue" or "DormWorkTimePerVitality" or "DormWorkRewardPerVitality")
        .ToDictionary(row => row.Key, row => int.Parse(row.Value, CultureInfo.InvariantCulture));

    private static DormWorkData ResponseWork(PlayerDormWork work) => new()
    {
        CharacterId = work.CharacterId, WorkPos = work.WorkPos, WorkEndTime = work.WorkEndTime,
        DormitoryNum = work.DormitoryNum, RewardNum = work.RewardNum, ResetCount = work.ResetCount
    };

    private static NotifyDormitoryData.NotifyDormitoryDataWork Work(PlayerDormWork work) => new()
    {
        CharacterId = work.CharacterId, WorkPos = work.WorkPos, WorkEndTime = work.WorkEndTime,
        DormitoryNum = work.DormitoryNum, RewardNum = work.RewardNum, ResetCount = work.ResetCount
    };
}
