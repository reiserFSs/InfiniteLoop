using System.Globalization;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.dormitory;
using AscNet.Table.V2.share.dormitory.character;
using AscNet.Table.V2.share.dormitory.furniture;
using AscNet.Table.V2.share.reward;
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
public class DormitoryListResponse
{
    public int Code;
    public List<NotifyDormitoryData.NotifyDormitoryDataDormitory> DormitoryList = [];
}

[MessagePackObject(true)]
public class DormOutRequest { }

[MessagePackObject(true)]
public class DormPutCharacterRequest
{
    public int DormitoryId;
    public List<uint> CharacterIds = [];
}

[MessagePackObject(true)]
public class DormPutCharacterResponse
{
    public int Code;
    public List<uint> SuccessIds = [];
}

[MessagePackObject(true)]
public class DormRemoveCharacterRequest { public List<uint> CharacterIds = []; }

[MessagePackObject(true)]
public class DormRemoveCharacterResult
{
    public int DormitoryId;
    public uint CharacterId;
}

[MessagePackObject(true)]
public class DormRemoveCharacterResponse
{
    public int Code;
    public List<DormRemoveCharacterResult> SuccessList = [];
}

[MessagePackObject(true)]
public class NotifyDormCharacterRecovery
{
    public int ChangeType;
    public List<DormCharacterRecovery> Recoveries = [];
}

[MessagePackObject(true)]
public class DormCharacterRecovery
{
    public uint CharacterId;
    public int MoodSpeed;
    public int VitalitySpeed;
}

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

internal partial class DormModule
{
    private const int DormRequestDataInvalid = 20060040;
    private const int PutCharacterRecoveryChangeType = 2;
    private static readonly Lazy<ILookup<uint, DormCharacterRecoveryTable>> CharacterRecoveryTiers = new(() =>
        TableReaderV2.Parse<DormCharacterRecoveryTable>().OrderByDescending(row => row.Pre)
            .ToLookup(row => (uint)row.CharacterId));

    public static NotifyDormitoryData BuildLoginData(Session session)
    {
        Dictionary<string, int> config = Config();
        bool changed = false;

        Dictionary<uint, DormCharacterFondleTable> fondles = TableReaderV2.Parse<DormCharacterFondleTable>()
            .ToDictionary(row => (uint)row.CharacterId);
        uint now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (uint characterId in session.character.Characters.Select(character => character.Id).Distinct())
        {
            PlayerDormCharacter? character = session.player.Dorm.Characters.FirstOrDefault(saved => saved.CharacterId == characterId);
            if (character is null)
            {
                fondles.TryGetValue(characterId, out DormCharacterFondleTable? fondle);
                character = new PlayerDormCharacter
                {
                    CharacterId = characterId,
                    Mood = config.GetValueOrDefault("DormMoodInitValue"),
                    Vitality = config.GetValueOrDefault("DormVitalityInitValue"),
                    LeftFondleCount = fondle?.MaxCount ?? 0,
                    LastFondleRecoveryTime = now,
                    LastRecoveryTime = now
                };
                session.player.Dorm.Characters.Add(character);
                changed = true;
            }
            else
                changed |= NormalizeFondle(character, fondles.GetValueOrDefault(characterId), now);
        }
        changed |= NormalizeFreeRooms(session.player.Dorm);
        changed |= session.player.Dorm.NormalizeFurnitureIds();
        changed |= SettleRecovery(session, now);
        changed |= RecalculateRecovery(session, now);
        changed |= NormalizeWorkRefresh(session.player.Dorm, now);
        changed |= EvaluateEvents(session, now);

        if (changed)
            session.player.Save();

        return new NotifyDormitoryData
        {
            FurnitureCreateList = session.player.Dorm.FurnitureCreateList.Select(create => new
            {
                create.Pos, create.EndTime, Furniture = create.Furniture is null ? null : new
                {
                    create.Furniture.Id, create.Furniture.ConfigId, create.Furniture.X, create.Furniture.Y, create.Furniture.Angle,
                    create.Furniture.DormitoryId, create.Furniture.Addition, create.Furniture.AttrList, create.Furniture.BaseAttrList, create.Furniture.IsLocked
                },
                create.Count
            }).ToList<dynamic>(),
            WorkList = session.player.Dorm.WorkList.Select(Work).ToList(),
            FurnitureUnlockList = session.player.Dorm.FurnitureUnlocks.Concat(session.player.Dorm.Furniture.Select(furniture => furniture.ConfigId)).Distinct().ToList(),
            SnapshotTimes = session.player.Dorm.SnapshotTimes,
            DormitoryList = session.player.Dorm.Rooms.Select(RoomData).ToList(),
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
            }).ToList(),
            Layouts = session.player.Dorm.Layouts.Select(layout => new { layout.LayoutId, layout.CreateTime, layout.Name, FurnitureList = layout.FurnitureList.Select(furniture => new { furniture.ConfigId, furniture.X, furniture.Y, furniture.Angle }).ToList() }).ToList<dynamic>(),
            BindRelations = session.player.Dorm.BindRelations.Select(bind => new { bind.LayoutId, bind.DormitoryId }).ToList<dynamic>(),
            DormQuestData = BuildQuestLoginData(session)
        };
    }

    [RequestPacketHandler("DormEnterRequest")]
    public static void DormEnterRequestHandler(Session session, Packet.Request packet)
    {
        ResumePendingDormRewards(session);
        uint now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        bool changed = SettleRecovery(session, now);
        bool recoveryChanged = RecalculateRecovery(session, now);
        changed |= recoveryChanged;
        bool workRefreshed = NormalizeWorkRefresh(session.player.Dorm, now);
        changed |= workRefreshed;
        changed |= EvaluateEvents(session, now);
        if (changed) session.player.Save();
        session.IsInDorm = true;
        if (workRefreshed) SendWorkRefreshTime(session);
        session.DormTaskStateDirty = true;
        TaskModule.RecordConditionType(session, 29014);
        session.SendResponse(new DormEnterResponse { CharacterEvents = EventResponses(session) }, packet.Id);
        if (recoveryChanged) SendCharacterRecovery(session);
        SendCharacterAttrs(session);
    }

    [RequestPacketHandler("DormOutRequest")]
    public static void DormOutRequestHandler(Session session, Packet.Request packet) => session.IsInDorm = false;

    internal static void RefreshResidentRecovery(Session session)
    {
        if (!session.IsInDorm)
            return;

        uint now = Now();
        bool recoveryChanged = SettleRecovery(session, now);
        bool workRefreshed = NormalizeWorkRefresh(session.player.Dorm, now);
        if (!recoveryChanged && !workRefreshed)
            return;

        if (workRefreshed) TaskModule.EnsureMissionResets(session, now);
        session.player.Save();
        if (workRefreshed) SendWorkRefreshTime(session);
        if (recoveryChanged) SendCharacterAttrs(session);
    }

    [RequestPacketHandler("DormPutCharacterRequest")]
    public static void DormPutCharacterRequestHandler(Session session, Packet.Request packet)
    {
        DormPutCharacterRequest request = packet.Deserialize<DormPutCharacterRequest>();
        PlayerDormRoom? room = session.player.Dorm.Rooms.FirstOrDefault(saved => saved.Id == request.DormitoryId);
        DormitoryTable? config = TableReaderV2.Parse<DormitoryTable>().FirstOrDefault(row => row.Id == request.DormitoryId);
        HashSet<uint> owned = session.character.Characters.Select(character => character.Id).ToHashSet();
        List<PlayerDormCharacter> characters = request.CharacterIds
            .Select(id => session.player.Dorm.Characters.FirstOrDefault(character => character.CharacterId == id))
            .Where(character => character is not null)
            .Cast<PlayerDormCharacter>()
            .ToList();
        int retained = session.player.Dorm.Characters.Count(character =>
            character.DormitoryId == request.DormitoryId && !request.CharacterIds.Contains(character.CharacterId));
        bool valid = room is not null && config is not null && request.CharacterIds.Count > 0
            && request.CharacterIds.Distinct().Count() == request.CharacterIds.Count
            && characters.Count == request.CharacterIds.Count
            && request.CharacterIds.All(owned.Contains)
            && characters.All(character => character.DormitoryId == -1)
            && characters.All(character => session.player.Dorm.WorkList.All(work =>
                work.WorkEndTime == 0 || work.CharacterId != character.CharacterId))
            && retained + characters.Count <= config.CharCapacity;
        if (!valid)
        {
            session.SendResponse(new DormPutCharacterResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SettleRecovery(session, now);
        foreach (PlayerDormCharacter character in characters)
        {
            character.DormitoryId = request.DormitoryId;
            TrackResidentMood(character, now);
            character.LastRecoveryTime = now;
        }
        RecalculateRecovery(session, now);
        session.player.Save();
        session.DormTaskStateDirty = true;
        TaskModule.SendConditionTypeSync(session, 29002);
        SendCharacterRecovery(session);
        session.SendResponse(new DormPutCharacterResponse { SuccessIds = request.CharacterIds }, packet.Id);
    }

    [RequestPacketHandler("DormRemoveCharacterRequest")]
    public static void DormRemoveCharacterRequestHandler(Session session, Packet.Request packet)
    {
        DormRemoveCharacterRequest request = packet.Deserialize<DormRemoveCharacterRequest>();
        List<PlayerDormCharacter> characters = request.CharacterIds
            .Select(id => session.player.Dorm.Characters.FirstOrDefault(character => character.CharacterId == id))
            .Where(character => character is not null)
            .Cast<PlayerDormCharacter>()
            .ToList();
        bool valid = request.CharacterIds.Count > 0
            && request.CharacterIds.Distinct().Count() == request.CharacterIds.Count
            && characters.Count == request.CharacterIds.Count
            && characters.All(character => character.DormitoryId > 0)
            && characters.All(character => session.player.Dorm.WorkList.All(work =>
                work.WorkEndTime == 0 || work.CharacterId != character.CharacterId));
        if (!valid)
        {
            session.SendResponse(new DormRemoveCharacterResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        List<DormRemoveCharacterResult> removed = characters.Select(character => new DormRemoveCharacterResult
        {
            CharacterId = character.CharacterId,
            DormitoryId = character.DormitoryId
        }).ToList();
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SettleRecovery(session, now);
        foreach (PlayerDormCharacter character in characters)
        {
            character.DormitoryId = -1;
            TrackResidentMood(character, now);
            character.LastRecoveryTime = now;
        }
        RecalculateRecovery(session, now);
        session.player.Save();
        session.DormTaskStateDirty = true;
        SendCharacterRecovery(session);
        session.SendResponse(new DormRemoveCharacterResponse { SuccessList = removed }, packet.Id);
    }

    [RequestPacketHandler("DormWorkRequest")]
    public static void DormWorkRequestHandler(Session session, Packet.Request packet)
    {
        DormWorkRequest request = packet.Deserialize<DormWorkRequest>();
        uint now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        bool settled = SettleRecovery(session, now);
        bool workRefreshed = NormalizeWorkRefresh(session.player.Dorm, now);
        Dictionary<string, int> config = Config();
        List<DormCharacterWorkTable> table = TableReaderV2.Parse<DormCharacterWorkTable>();
        int unlocked = session.player.Dorm.Rooms.Count;
        DormCharacterWorkTable? work = table.FirstOrDefault(row => row.DormitoryNum == unlocked);
        HashSet<uint> owned = session.character.Characters.Select(character => character.Id).ToHashSet();
        bool valid = request.Works.Count > 0 && work is not null
            && request.Works.Select(entry => entry.CharacterId).Distinct().Count() == request.Works.Count
            && request.Works.Select(entry => entry.WorkPos).Distinct().Count() == request.Works.Count
            && request.Works.All(entry => entry.WorkPos > 0 && entry.WorkPos <= work!.Seat && owned.Contains(entry.CharacterId))
            && request.Works.All(entry => session.player.Dorm.WorkList.Where(active => active.WorkEndTime > 0).All(active => active.WorkPos != entry.WorkPos && active.CharacterId != entry.CharacterId))
            && request.Works.All(entry => session.player.Dorm.Characters.FirstOrDefault(character => character.CharacterId == entry.CharacterId) is { DormitoryId: > 0 } character
                && character.Mood >= work!.Mood && character.Vitality >= work.Vitality);
        if (!valid)
        {
            if (settled || workRefreshed) session.player.Save();
            if (workRefreshed) SendWorkRefreshTime(session);
            session.SendResponse(new DormWorkResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }
        int timeMultiplier = config.GetValueOrDefault("DormWorkTimePerVitality");
        List<PlayerDormWork> added = [];
        foreach (DormWork entry in request.Works)
        {
            PlayerDormCharacter character = session.player.Dorm.Characters.First(character => character.CharacterId == entry.CharacterId);
            int count = character.Vitality / work!.Vitality;
            character.Vitality = checked(character.Vitality - checked(count * work.Vitality));
            added.Add(new PlayerDormWork
            {
                CharacterId = entry.CharacterId, WorkPos = entry.WorkPos,
                WorkEndTime = checked(now + checked((uint)checked((long)count * work.Time * timeMultiplier))),
                DormitoryNum = work.DormitoryNum, RewardNum = count,
                ResetCount = session.player.Dorm.WorkList.Where(saved => saved.WorkPos == entry.WorkPos)
                    .Select(saved => saved.ResetCount).DefaultIfEmpty(-1).Max() + 1,
                ClaimKey = Guid.NewGuid().ToString("N")
            });
        }
        session.player.Dorm.WorkList.RemoveAll(saved => saved.WorkEndTime == 0
            && added.Any(entry => entry.WorkPos == saved.WorkPos || entry.CharacterId == saved.CharacterId));
        session.player.Dorm.WorkList.AddRange(added);
        session.player.Save();
        if (workRefreshed) SendWorkRefreshTime(session);
        foreach (PlayerDormWork entry in added)
        {
            session.SendPush(new NotifyCharacterVitality { CharacterId = entry.CharacterId, Vitality = session.player.Dorm.Characters.First(character => character.CharacterId == entry.CharacterId).Vitality / 100 });
            TaskModule.RecordTableDrivenProgress(session, [(29004, null, 1)]);
        }
        session.SendResponse(new DormWorkResponse { WorkList = added.Select(ResponseWork).ToList() }, packet.Id);
    }

    [RequestPacketHandler("DormWorkRewardRequest")]
    public static void DormWorkRewardRequestHandler(Session session, Packet.Request packet)
    {
        DormWorkRewardRequest request = packet.Deserialize<DormWorkRewardRequest>();
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Dictionary<int, DormCharacterWorkTable> workByRoom = TableReaderV2.Parse<DormCharacterWorkTable>().ToDictionary(row => row.DormitoryNum);
        int multiplier = Config().GetValueOrDefault("DormWorkRewardPerVitality");
        List<PlayerDormWork> work = request.PosList
            .Select(pos => session.player.Dorm.WorkList.FirstOrDefault(entry => entry.WorkPos == pos))
            .OfType<PlayerDormWork>()
            .ToList();
        foreach (PlayerDormWork entry in work.Where(entry => entry.WorkEndTime > 0 && entry.WorkEndTime <= now && string.IsNullOrEmpty(entry.ClaimKey)))
            entry.ClaimKey = Guid.NewGuid().ToString("N");
        bool valid = work.Count == request.PosList.Count && request.PosList.Count > 0
            && request.PosList.Distinct().Count() == request.PosList.Count
            && work.All(entry => workByRoom.ContainsKey(entry.DormitoryNum)
                && (entry.WorkEndTime > 0 && entry.WorkEndTime <= now
                    || entry.WorkEndTime == 0 && session.player.Dorm.PendingRewards.Any(
                        pending => pending.Key == WorkClaimKey(session, entry))));
        if (!valid)
        {
            session.SendResponse(new DormWorkRewardResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        List<DormWorkReward> rewards = work.Select(entry =>
        {
            DormCharacterWorkTable row = workByRoom[entry.DormitoryNum];
            return new DormWorkReward { WorkPos = entry.WorkPos, ItemId = row.ItemId, ItemNum = checked(entry.RewardNum * multiplier), ResetCount = entry.ResetCount };
        }).ToList();
        List<PlayerDormPendingReward> pending = work.Select((entry, index) =>
            session.player.Dorm.PendingRewards.FirstOrDefault(value => value.Key == WorkClaimKey(session, entry))
            ?? new PlayerDormPendingReward
            {
                Key = WorkClaimKey(session, entry),
                Goods = [new PlayerDormPendingRewardItem { TemplateId = rewards[index].ItemId, Count = rewards[index].ItemNum }]
            }).ToList();
        Dictionary<PlayerDormWork, uint> previousEndTimes = work.ToDictionary(entry => entry, entry => entry.WorkEndTime);
        List<PlayerDormWork> completed = work.Where(entry => entry.WorkEndTime > 0).ToList();
        foreach (PlayerDormWork entry in completed)
            entry.WorkEndTime = 0;
        foreach (PlayerDormPendingReward entry in pending.Where(entry => !session.player.Dorm.PendingRewards.Contains(entry)))
            session.player.Dorm.PendingRewards.Add(entry);
        if (completed.Count > 0)
        {
            try
            {
                session.player.SaveChecked();
            }
            catch
            {
                foreach (PlayerDormWork entry in completed)
                    entry.WorkEndTime = previousEndTimes[entry];
                session.player.Dorm.PendingRewards.RemoveAll(entry => pending.Contains(entry));
                session.SendResponse(new DormWorkRewardResponse { Code = DormRequestDataInvalid }, packet.Id);
                return;
            }
        }

        RewardApplicationResult grant;
        try
        {
            grant = RewardHandler.ApplyRewardsOnceAndPersist(pending.Select(entry =>
                new RewardGrant(entry.Key, entry.Goods.Select(item =>
                    new RewardGoodsTable { Id = item.Id, TemplateId = item.TemplateId, Count = item.Count, Params = item.Params.ToList() }).ToList())).ToList(), session);
        }
        catch
        {
            session.SendResponse(new DormWorkRewardResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        session.player.Dorm.PendingRewards.RemoveAll(entry => pending.Contains(entry));
        try
        {
            session.player.SaveChecked();
        }
        catch
        {
            session.player.Dorm.PendingRewards.AddRange(pending.Where(entry => !session.player.Dorm.PendingRewards.Contains(entry)));
            session.SendResponse(new DormWorkRewardResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }
        grant.SendPushes(session);
        session.SendResponse(new DormWorkRewardResponse { WorkRewards = rewards }, packet.Id);
    }

    private static readonly Lazy<List<RewardGoodsTable>> DefaultDormEventReward = new(() =>
    {
        Dictionary<int, RewardGoodsTable> goods = TableReaderV2.Parse<RewardGoodsTable>().ToDictionary(row => row.Id);
        return TableReaderV2.Parse<RewardTable>()
            .Select(row => row.SubIds.Select(id => goods.GetValueOrDefault(id)).OfType<RewardGoodsTable>().ToList())
            .FirstOrDefault(reward => reward.Count == 1 && reward[0].TemplateId == Inventory.DormCoin) ?? [];
    });

    private static List<RewardGoodsTable> DormEventReward(int rewardId)
    {
        List<RewardGoodsTable> reward = RewardHandler.GetRewardGoods(rewardId);
        return reward.Count > 0 ? reward : DefaultDormEventReward.Value;
    }

    [RequestPacketHandler("DormCharacterFinishAllEventRequest")]
    public static void DormCharacterFinishAllEventRequestHandler(Session session, Packet.Request packet)
    {
        List<(PlayerDormCharacter Character, PlayerDormEvent Event)> events = session.player.Dorm.Characters
            .SelectMany(character => character.EventList.Select(evt => (character, evt)))
            .ToList();
        if (events.Count == 0)
        {
            session.SendResponse(new DormCharacterFinishAllEventResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        Dictionary<int, DormCharacterEventTable> rows = TableReaderV2.Parse<DormCharacterEventTable>()
            .ToDictionary(row => row.EventId);
        List<((PlayerDormCharacter Character, PlayerDormEvent Event) Entry, RewardGrant Grant)> resolved = events
            .Where(entry => rows.ContainsKey(entry.Event.EventId))
            .Select(entry => (Entry: entry, Goods: DormEventReward(rows[entry.Event.EventId].FinishReward)))
            .Where(entry => entry.Goods.Count > 0)
            .Select(entry => (entry.Entry, new RewardGrant(
                $"dorm-event:{session.player.PlayerData.Id}:{entry.Entry.Character.CharacterId}:{entry.Entry.Event.EventId}:{entry.Entry.Event.EndTime}",
                entry.Goods)))
            .ToList();
        if (resolved.Count == 0)
        {
            session.SendResponse(new DormCharacterFinishAllEventResponse { Code = EventRewardTemplateMissing }, packet.Id);
            return;
        }

        RewardApplicationResult rewards;
        try
        {
            rewards = RewardHandler.ApplyRewardsOnceAndPersist(resolved.Select(entry => entry.Grant).ToList(), session);
            foreach (((PlayerDormCharacter character, PlayerDormEvent evt), _) in resolved)
                character.EventList.Remove(evt);
            session.player.SaveChecked();
        }
        catch
        {
            foreach (((PlayerDormCharacter character, PlayerDormEvent evt), _) in resolved)
                if (!character.EventList.Contains(evt))
                    character.EventList.Add(evt);
            session.SendResponse(new DormCharacterFinishAllEventResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }

        rewards.SendPushes(session);
        session.SendResponse(new DormCharacterFinishAllEventResponse
        {
            MoodChange = resolved.Select(entry => entry.Entry.Character.CharacterId).Distinct().ToDictionary(id => id, _ => 0),
            RewardGoods = rewards.RewardGoods
        }, packet.Id);
    }

    [RequestPacketHandler("DormitoryListRequest")]
    public static void DormitoryListRequestHandler(Session session, Packet.Request packet) =>
        session.SendResponse(new DormitoryListResponse
        {
            DormitoryList = session.player.Dorm.Rooms.Select(RoomData).ToList()
        }, packet.Id);

    private static void SendCharacterRecovery(Session session) => session.SendPush(new NotifyDormCharacterRecovery
    {
        ChangeType = PutCharacterRecoveryChangeType,
        Recoveries = session.player.Dorm.Characters.Select(character => new DormCharacterRecovery
        {
            CharacterId = character.CharacterId,
            MoodSpeed = character.MoodSpeed,
            VitalitySpeed = character.VitalitySpeed
        }).ToList()
    });

    private static void SendCharacterAttrs(Session session) => session.SendPush(new NotifyCharacterAttr
    {
        AttrList = session.player.Dorm.Characters.Select(character => new DormCharacterAttr
        {
            CharacterId = character.CharacterId, Mood = character.Mood / 100, Vitality = character.Vitality / 100
        }).ToList()
    });

    private static bool SettleRecovery(Session session, uint now)
    {
        bool changed = false;
        foreach (PlayerDormCharacter character in session.player.Dorm.Characters)
        {
            changed |= TrackResidentMood(character, now);
            if (character.LastRecoveryTime == 0 || now < character.LastRecoveryTime)
            {
                character.LastRecoveryTime = now;
                changed = true;
                continue;
            }
            uint elapsed = now - character.LastRecoveryTime;
            uint hours = elapsed / 3600;
            if (hours == 0) continue;
            int moodBefore = character.Mood;
            character.Mood = (int)Math.Clamp((long)character.Mood + (long)hours * character.MoodSpeed, 0, 10000);
            character.Vitality = (int)Math.Clamp((long)character.Vitality + (long)hours * character.VitalitySpeed, 0, 10000);
            if (character.DormitoryId > 0 && moodBefore < ResidentMoodTarget.Value && character.Mood >= ResidentMoodTarget.Value)
            {
                int missing = ResidentMoodTarget.Value - moodBefore;
                long firstHour = (missing + (long)character.MoodSpeed - 1) / character.MoodSpeed;
                character.HighMoodSince = checked(character.LastRecoveryTime + (uint)(firstHour * 3600));
            }
            changed |= TrackResidentMood(character, now);
            character.LastRecoveryTime = now - elapsed % 3600;
            changed = true;
        }
        session.DormTaskStateDirty |= changed;
        return changed;
    }

    private static bool NormalizeFreeRooms(PlayerDormState state)
    {
        HashSet<uint> definedFurniture = TableReaderV2.Parse<FurnitureTable>().Select(row => (uint)row.Id).ToHashSet();
        bool changed = false;
        foreach (DormitoryTable table in TableReaderV2.Parse<DormitoryTable>().Where(row => row.IsFree == 1))
        {
            PlayerDormRoom? room = state.Rooms.FirstOrDefault(saved => saved.Id == table.Id);
            if (room is null)
            {
                List<PlayerDormFurniture> furniture = InitialFurniture(table, state);
                state.Rooms.Add(new PlayerDormRoom { Id = (uint)table.Id, Name = table.InitName ?? string.Empty });
                state.Furniture.AddRange(furniture);
                foreach (uint id in furniture.Select(furniture => furniture.ConfigId).Distinct())
                    if (!state.FurnitureUnlocks.Contains(id)) state.FurnitureUnlocks.Add(id);
                changed = true;
                continue;
            }

            if (state.Characters.Any(character => character.DormitoryId == table.Id)
                || state.Furniture.Any(furniture => furniture.DormitoryId == table.Id))
                continue;

            List<(uint Id, string Position)> initial = table.InitFurniture.Zip(table.InitFurniturePos, (id, position) => (Id: (uint)id, Position: position))
                .Where(furniture => furniture.Id > 0 && definedFurniture.Contains(furniture.Id)).ToList();
            List<PlayerDormFurniture> available = state.Furniture.Where(furniture => furniture.DormitoryId == 0).ToList();
            List<PlayerDormFurniture> matched = [];
            foreach ((uint id, _) in initial)
            {
                PlayerDormFurniture? furniture = available.FirstOrDefault(candidate => candidate.ConfigId == id);
                if (furniture is null) break;
                available.Remove(furniture);
                matched.Add(furniture);
            }
            if (matched.Count != initial.Count || initial.Count == 0) continue;

            for (int index = 0; index < matched.Count; index++)
            {
                int[] position = initial[index].Position.Split('|').Select(int.Parse).ToArray();
                matched[index].DormitoryId = table.Id;
                matched[index].X = position[0];
                matched[index].Y = position[1];
                matched[index].Angle = position[2];
            }
            changed = true;
        }

        foreach (PlayerDormFurniture furniture in state.Furniture.Where(furniture => furniture.DormitoryId == 0))
        {
            furniture.DormitoryId = -1;
            changed = true;
        }
        return changed;
    }

    private static bool RecalculateRecovery(Session session, uint now)
    {
        PlayerDormState dorm = session.player.Dorm;
        HashSet<int> rooms = dorm.Rooms.Select(room => (int)room.Id).ToHashSet();
        var furnitureByRoom = dorm.Furniture.Where(piece => rooms.Contains(piece.DormitoryId))
            .ToLookup(piece => piece.DormitoryId);
        bool changed = false;
        foreach (var residents in dorm.Characters.GroupBy(character => character.DormitoryId))
        {
            bool hasRoom = rooms.Contains(residents.Key);
            var furniture = furnitureByRoom[residents.Key];
            List<int> attributes = RoomAttributes(furniture);
            int total = attributes.Sum();
            int moodBonus = 0;
            int vitalityBonus = 0;
            foreach (PlayerDormFurniture piece in furniture)
            {
                var addition = DormAdditionalAttrs.Value.GetValueOrDefault(piece.Addition);
                if (addition is { AddType: 1 })
                    vitalityBonus += addition.AddValue.ElementAtOrDefault(0);
                else if (addition is not null && addition.AddType != 2)
                    moodBonus += addition.AddValue.ElementAtOrDefault(0);
            }

            foreach (PlayerDormCharacter character in residents)
            {
                DormCharacterRecoveryTable? row = hasRoom
                    ? CharacterRecoveryTiers.Value[character.CharacterId].FirstOrDefault(tier =>
                        (tier.AttrTotal ?? 0) <= total
                        && tier.AttrCondition.Zip(attributes).All(pair => pair.First <= pair.Second))
                    : null;
                int moodRate = row is null ? 0 : row.MoodRecoveryType == 1 ? -row.MoodRecovery : row.MoodRecovery;
                int vitalityRate = row is null ? 0 : row.VitalityRecoveryType == 1 ? -row.VitalityRecovery : row.VitalityRecovery;
                int moodSpeed = checked((int)((long)moodRate * (100 + moodBonus) / 100));
                int vitalitySpeed = checked((int)((long)vitalityRate * (100 + vitalityBonus) / 100));
                if (character.MoodSpeed == moodSpeed && character.VitalitySpeed == vitalitySpeed)
                    continue;

                character.MoodSpeed = moodSpeed;
                character.VitalitySpeed = vitalitySpeed;
                character.LastRecoveryTime = now;
                changed = true;
            }
        }
        return changed;
    }

    private static void ResumePendingDormRewards(Session session)
    {
        List<PlayerDormPendingReward> pending = session.player.Dorm.PendingRewards.ToList();
        if (pending.Count == 0) return;
        RewardApplicationResult rewards;
        try
        {
            rewards = RewardHandler.ApplyRewardsOnceAndPersist(pending.Select(entry =>
                new RewardGrant(entry.Key, entry.Goods.Select(item =>
                    new RewardGoodsTable { Id = item.Id, TemplateId = item.TemplateId, Count = item.Count, Params = item.Params.ToList() }).ToList())).ToList(), session);
        }
        catch { return; }
        session.player.Dorm.PendingRewards.RemoveAll(entry => pending.Contains(entry));
        try { session.player.SaveChecked(); }
        catch
        {
            session.player.Dorm.PendingRewards.AddRange(pending.Where(entry => !session.player.Dorm.PendingRewards.Contains(entry)));
            return;
        }
        rewards.SendPushes(session);
    }

    private static Dictionary<string, int> Config() => TableReaderV2.Parse<ConfigTable>()
        .Where(row => row.Key is "DormMoodInitValue" or "DormMoodMaxValue" or "DormVitalityInitValue" or "DormWorkTimePerVitality" or "DormWorkRewardPerVitality"
            or "DormMaxCreateCount" or "DormMaxRecycleCount" or "DormMaxRemouldCount" or "DormMaxTotalFurnitureCount"
            or "DormEventRefreshTime" or "DailyResetTimestamp")
        .ToDictionary(row => row.Key, row => int.Parse(row.Value, CultureInfo.InvariantCulture));

    internal static uint NextDailyRefreshTime(uint now)
    {
        const uint day = 24 * 60 * 60;
        uint reset = checked((uint)Config().GetValueOrDefault("DailyResetTimestamp"));
        uint next = checked(now - now % day + reset);
        return next > now ? next : checked(next + day);
    }

    internal static void SendWorkRefreshTime(Session session) =>
        session.SendPush(new NotifyWorkNextRefreshTime { NextRefreshTime = session.player.Dorm.WorkNextRefreshTime });

    private static bool NormalizeWorkRefresh(PlayerDormState dorm, uint now)
    {
        if (dorm.WorkNextRefreshTime != 0 && now < dorm.WorkNextRefreshTime)
            return false;
        dorm.WorkList.RemoveAll(work => work.WorkEndTime == 0);
        dorm.WorkNextRefreshTime = NextDailyRefreshTime(now);
        return true;
    }

    private static string WorkClaimKey(Session session, PlayerDormWork work) =>
        string.IsNullOrEmpty(work.ClaimKey)
            ? $"dorm-work:{session.player.PlayerData.Id}:{work.CharacterId}:{work.WorkPos}:{work.ResetCount}"
            : work.ClaimKey;

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
