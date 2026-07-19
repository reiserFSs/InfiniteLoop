using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre6;
using AscNet.GameServer.Game;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.theatre6pvp;
using MessagePack;

using MongoDB.Bson;
namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class Theatre6FileData { public int SlotId { get; set; } public int CharacterId { get; set; } public int Score { get; set; } public List<int> BuildTags { get; set; } = new(); public List<Theatre6AttrData> Attrs { get; set; } = new(); public List<Theatre6SkillData> Skills { get; set; } = new(); public List<Theatre6AttrPackData> AttrPacks { get; set; } = new(); public List<Theatre6BuffData> Buffs { get; set; } = new(); public int FashionId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6AttrData { public int AttrId { get; set; } public int Value { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6SkillData { public int SlotType { get; set; } public int Position { get; set; } public int SkillId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6AttrPackData { public int PackId { get; set; } public int Num { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6BuffData { public int BuffId { get; set; } public int TriggerCount { get; set; } public int AddMagic { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6FileSlot { public int CharacterId { get; set; } public int SlotId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6EndGameRequest { public int ModeId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6EndGameResponse { public Theatre6SettleData? SettleData { get; set; } public Theatre6StoryModeSaveDb? StoryModeSaveDb { get; set; } public int Code { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6SaveFileRequest { public int ModeId { get; set; } public int SlotId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6SaveFileResponse { public Theatre6FileData? FileData { get; set; } public int Code { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6SettleData { public bool IsWin { get; set; } public Theatre6FileData FileData { get; set; } = new(); public int CurHeath { get; set; } public int MaxHeath { get; set; } public int CurSan { get; set; } public int MaxSan { get; set; } public List<Theatre6FightRecord> FightRecords { get; set; } = new(); public List<Theatre6SettleReward>? RewardList { get; set; } public Dictionary<int,int> PassStageRecords { get; set; } = new(); public Dictionary<int,int> PassDiffRecords { get; set; } = new(); }
[MessagePackObject(true)] public sealed class Theatre6FightRecord { public int DifficultyType { get; set; } public int FightResultType { get; set; } public int FightId { get; set; } public int MonsterId { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6SettleReward { public int Id { get; set; } public int Type { get; set; } public int Count { get; set; } public bool IsFirst { get; set; } }
[MessagePackObject(true)] public sealed class Theatre6StoryModeSaveDb { public List<Theatre6StoryLineData> StoryLineDatas { get; set; } = new(); public List<int> StoryIds { get; set; } = new(); }
[MessagePackObject(true)] public sealed class Theatre6StoryLineData { public int StoryLineId { get; set; } public int StageIndex { get; set; } public bool IsCompletedBefore { get; set; } public bool IsBuy { get; set; } public List<int> BuyIndex { get; set; } = new(); }

[MessagePackObject(true)]
public sealed class NotifyTheatre6ActivityData
{
    public int ActivityId { get; set; }
    public int CurrentMode { get; set; }
    public object? PlayModeDataDb { get; set; }
    public object? StoryModeDataDb { get; set; }
    public List<Theatre6FileData> FileDatas { get; set; } = new();
    public List<Theatre6StoryLineData> StoryLineDatas { get; set; } = new();
    public List<int> PassStageId { get; set; } = new();
    public List<int> CharacterIds { get; set; } = new();
    public Theatre6PlayModeSaveDb PlayModeSaveDb { get; set; } = new();
    public Theatre6StoryModeSaveDb StoryModeSaveDb { get; set; } = new();
    public Dictionary<int, int> PassStageRecords { get; set; } = new();
    public Dictionary<int, int> PassDiffRecords { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class Theatre6PlayModeSaveDb
{
    public List<int> StoryIds { get; set; } = new();
    public int TalentLevel { get; set; }
    public int TalentExp { get; set; }
}

internal static class Theatre6Module
{
    internal const int NotOpen = 1;
    internal const int InvalidSaveFile = 2;

    private static readonly Lazy<Dictionary<int, Theatre6CharacterTable>> Characters = new(() => TableReaderV2.Parse<Theatre6CharacterTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, Theatre6CharacterFashionTable>> Fashions = new(() => TableReaderV2.Parse<Theatre6CharacterFashionTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, Theatre6AttrTable>> Attrs = new(() => TableReaderV2.Parse<Theatre6AttrTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, Theatre6SkillTable>> Skills = new(() => TableReaderV2.Parse<Theatre6SkillTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, Theatre6AttrPackTable>> AttrPacks = new(() => TableReaderV2.Parse<Theatre6AttrPackTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<HashSet<int>> Buffs = new(() => TableReaderV2.Parse<Theatre6StageBuffTable>().Select(x => x.Id).ToHashSet());
    private static readonly Lazy<HashSet<int>> BuildTags = new(() => TableReaderV2.Parse<Theatre6BuildTagTable>().Select(x => x.Id).ToHashSet());
    private static readonly Lazy<int> MaxSaveFiles = new(() => Cfg("MaxSaveFileCount"));
    private static readonly Lazy<HashSet<int>> Activities = new(() => TableReaderV2.Parse<Theatre6ActivityTable>().Select(x => x.Id).ToHashSet());
    private readonly record struct Availability(int BaseActivityId, int PvpSeasonId, int TimeId, int RequiredLevel);
    private static readonly Lazy<Availability?> CurrentAvailability = new(LoadAvailability);

    private static Availability? LoadAvailability()
    {
        List<EventCatalogTable> catalog = TableReaderV2.Parse<EventCatalogTable>();
        List<SkipFunctionalTable> skips = TableReaderV2.Parse<SkipFunctionalTable>();
        Dictionary<int, FunctionalOpenTable> functions = TableReaderV2.Parse<FunctionalOpenTable>().ToDictionary(row => row.Id);
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        foreach (Theatre6PvpActivityTable season in TableReaderV2.Parse<Theatre6PvpActivityTable>().OrderBy(row => row.Id))
        {
            if (season.TimeId is not > 0)
                continue;

            EventCatalogTable? eventEntry = catalog.SingleOrDefault(row => row.TimeId == season.TimeId);
            SkipFunctionalTable? skip = eventEntry is null
                ? null
                : skips.SingleOrDefault(row => row.SkipId == eventEntry.SkipId && row.FunctionalId is > 0);
            if (skip?.FunctionalId is not int functionId
                || !functions.TryGetValue(functionId, out FunctionalOpenTable? function)
                || function.Condition.Count != 1
                || !conditions.TryGetValue(function.Condition[0], out ConditionTable? condition)
                || condition.Type != 10101
                || condition.Params.Count != 1
                || Activities.Value.Count != 1)
                continue;

            return new Availability(Activities.Value.Single(), season.Id, season.TimeId, condition.Params[0]);
        }

        return null;
    }

    internal static bool IsAvailable(Player player, DateTimeOffset now, out int pvpSeasonId, out int timeId)
    {
        Availability? availability = CurrentAvailability.Value;
        if (availability is not Availability value
            || player.PlayerData.Level < value.RequiredLevel
            || !ActivityScheduleService.IsOpen(value.TimeId, now))
        {
            pvpSeasonId = 0;
            timeId = 0;
            return false;
        }

        pvpSeasonId = value.PvpSeasonId;
        timeId = value.TimeId;
        return true;
    }

    internal static bool ReconcileAvailability(Player player, DateTimeOffset now)
    {
        if (!IsAvailable(player, now, out int pvpSeasonId, out int timeId)
            || CurrentAvailability.Value is not Availability availability)
            return false;

        Theatre6State state = player.Theatre6;
        bool changed = false;
        if (state.ActivityId != availability.BaseActivityId)
        {
            state.ActivityId = availability.BaseActivityId;
            changed = true;
        }
        if (state.Pvp.AuthorizedSeasonId != pvpSeasonId)
        {
            state.Pvp.AuthorizedSeasonId = pvpSeasonId;
            changed = true;
        }
        if (!state.Pvp.AuthorizedTimeIds.SequenceEqual([timeId]))
        {
            state.Pvp.AuthorizedTimeIds = [timeId];
            changed = true;
        }
        return changed;
    }

    internal static bool HasAuthorizedAvailability(Player player, DateTimeOffset now, out int pvpSeasonId)
    {
        if (!IsAvailable(player, now, out pvpSeasonId, out int timeId)
            || CurrentAvailability.Value is not Availability availability)
            return false;
        return player.Theatre6.ActivityId == availability.BaseActivityId
            && player.Theatre6.Pvp.AuthorizedSeasonId == pvpSeasonId
            && player.Theatre6.Pvp.AuthorizedTimeIds.Contains(timeId);
    }

    private static int Cfg(string key) => TableReaderV2.Parse<Theatre6ConfigTable>().FirstOrDefault(x => x.Key == key)?.Values.FirstOrDefault() ?? 0;
    private static Theatre6State Clone(Theatre6State value) => MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Theatre6State>(value.ToBson());
    private static void Commit(Session session, Theatre6State original, Theatre6State staged)
    {
        session.player.Theatre6 = staged;
        try { session.player.Save(); }
        catch { session.player.Theatre6 = original; throw; }
    }

    internal static NotifyTheatre6ActivityData? BuildNotify(Player player) =>
        BuildNotify(player, DateTimeOffset.UtcNow);

    internal static NotifyTheatre6ActivityData? BuildNotify(Player player, DateTimeOffset now)
    {
        Theatre6State state = player.Theatre6;
        if (!HasAuthorizedAvailability(player, now, out _)
            || state.ActivityId <= 0 || !Activities.Value.Contains(state.ActivityId))
            return null;

        List<Theatre6FileState> files = state.Files.Where(file => file.SlotId is >= 1 && file.SlotId <= MaxSaveFiles.Value).OrderBy(file => file.SlotId).Take(MaxSaveFiles.Value).ToList();
        Theatre6StoryModeSaveDb storySave = ToWire(state.StorySave);
        return new NotifyTheatre6ActivityData
        {
            ActivityId = state.ActivityId,
            FileDatas = files.Select(ToWire).ToList(),
            StoryLineDatas = storySave.StoryLineDatas.Select(line => new Theatre6StoryLineData
            {
                StoryLineId = line.StoryLineId,
                StageIndex = line.StageIndex,
                IsCompletedBefore = line.IsCompletedBefore,
                IsBuy = line.IsBuy,
                BuyIndex = line.BuyIndex.Distinct().Take(64).ToList()
            }).Take(64).ToList(),
            PassStageId = state.PassStageRecords.Keys.Order().Take(256).ToList(),
            CharacterIds = files.Select(file => file.CharacterId).Where(id => id > 0).Distinct().Order().ToList(),
            PlayModeSaveDb = new Theatre6PlayModeSaveDb { StoryIds = state.StorySave.StoryIds.Distinct().Take(256).ToList() },
            StoryModeSaveDb = storySave,
            PassStageRecords = new(state.PassStageRecords.Take(256).ToDictionary()),
            PassDiffRecords = new(state.PassDiffRecords.Take(256).ToDictionary())
        };
    }

    [RequestPacketHandler("Theatre6EndGameRequest")]
    public static void EndGame(Session session, Packet.Request packet)
    {
        Theatre6EndGameRequest request = packet.Deserialize<Theatre6EndGameRequest>();
        Theatre6State original = session.player.Theatre6;
        Theatre6State state = Clone(original);
        if (state.Settlements.TryGetValue(request.ModeId, out Theatre6SettlementState? receipt))
        {
            session.SendResponse(BuildEndResponse(receipt), packet.Id);
            return;
        }
        if (!state.ActiveRuns.TryGetValue(request.ModeId, out Theatre6RunState? run) || run.Settled || !TryFinalize(run, out Theatre6SettlementState? settlement))
        {
            session.SendResponse(new Theatre6EndGameResponse { Code = NotOpen }, packet.Id);
            return;
        }

        run.Settled = true;
        state.Settlements[request.ModeId] = settlement!;
        state.StorySave = Clone(settlement!.StorySave);
        MergeMax(state.PassStageRecords, settlement.PassStageRecords);
        MergeMax(state.PassDiffRecords, settlement.PassDiffRecords);
        Commit(session, original, state);
        session.SendResponse(BuildEndResponse(settlement), packet.Id);
    }

    [RequestPacketHandler("Theatre6SaveFileRequest")]
    public static void SaveFile(Session session, Packet.Request packet)
    {
        Theatre6SaveFileRequest request = packet.Deserialize<Theatre6SaveFileRequest>();
        Theatre6State original = session.player.Theatre6;
        Theatre6State state = Clone(original);
        if (request.SlotId < 1 || request.SlotId > MaxSaveFiles.Value || !state.Settlements.TryGetValue(request.ModeId, out Theatre6SettlementState? settlement))
        {
            session.SendResponse(new Theatre6SaveFileResponse { Code = InvalidSaveFile }, packet.Id);
            return;
        }
        Theatre6FileState archived = Clone(settlement.File); archived.SlotId = request.SlotId;
        List<Theatre6FileState> replacement = state.Files.Select(Clone).ToList();
        int index = replacement.FindIndex(x => x.CharacterId == archived.CharacterId && x.SlotId == archived.SlotId);
        if (index >= 0) replacement[index] = archived; else replacement.Add(archived);
        state.Files = replacement;
        Commit(session, original, state);
        session.SendResponse(new Theatre6SaveFileResponse { Code = 0, FileData = ToWire(archived) }, packet.Id);
    }

    private static bool TryFinalize(Theatre6RunState run, out Theatre6SettlementState? result)
    {
        result = null; Theatre6FileState file = Clone(run.File); file.SlotId = 0;
        if (!Characters.Value.TryGetValue(file.CharacterId, out Theatre6CharacterTable? character) || character.FashionIds != file.FashionId || !Fashions.Value.ContainsKey(file.FashionId)) return false;
        const int activeSkillSlotType = 2;
        const int bagSkillSlotType = 4;
        Dictionary<int,int> slotCaps = new() { [1] = 1, [2] = Cfg("ActiveSkillSlotLimit"), [3] = Cfg("InsertSkillSlotLimit"), [4] = Cfg("SkillBagSlotLimit") };
        long score = 0; HashSet<int> tags = new(); int activeSkillCount = 0;
        HashSet<int> attrIds = new();
        foreach (Theatre6AttrState attr in file.Attrs) { if (!attrIds.Add(attr.AttrId) || !Attrs.Value.TryGetValue(attr.AttrId, out Theatre6AttrTable? row) || attr.Value < row.Min || attr.Value > row.Max) return false; score = checked(score + (long)attr.Value * (row.SaveScore ?? 0)); }
        HashSet<(int,int)> slots = new();
        foreach (Theatre6SkillState skill in file.Skills) { if (!slotCaps.TryGetValue(skill.SlotType, out int cap) || skill.Position < 1 || skill.Position > cap || !slots.Add((skill.SlotType,skill.Position)) || !Skills.Value.TryGetValue(skill.SkillId, out Theatre6SkillTable? row) || (row.Character > 0 && row.Character != file.CharacterId)) return false; if (skill.SlotType != bagSkillSlotType) { score = checked(score + row.SaveScore); tags.UnionWith(row.BuildTags); } if (skill.SlotType == activeSkillSlotType) activeSkillCount++; }
        foreach (int baseSkill in character.BaseSkill.Skip(activeSkillCount)) { if (!Skills.Value.TryGetValue(baseSkill, out Theatre6SkillTable? row)) return false; score = checked(score + row.SaveScore); tags.UnionWith(row.BuildTags); }
        HashSet<int> packIds = new();
        foreach (Theatre6AttrPackState pack in file.AttrPacks) { if (!packIds.Add(pack.PackId) || !AttrPacks.Value.TryGetValue(pack.PackId, out Theatre6AttrPackTable? row) || pack.Num < 0 || pack.Num > row.LimitCount || (row.Character > 0 && row.Character != file.CharacterId)) return false; score = checked(score + (long)pack.Num * (row.SaveScore ?? 0)); tags.UnionWith(row.BuildTags); }
        if (file.Buffs.Any(x => x.TriggerCount < 0 || x.TriggerCount > Cfg("BuffMaxTriggerCount") || !Buffs.Value.Contains(x.BuffId)) || tags.Any(x => !BuildTags.Value.Contains(x)) || score > int.MaxValue) return false;
        file.Score = checked((int)score); file.BuildTags = tags.Order().ToList();
        result = new Theatre6SettlementState { ModeId = run.ModeId, IsWin = run.IsWin, File = file, CurHealth = run.CurHealth, MaxHealth = run.MaxHealth, CurSan = run.CurSan, MaxSan = run.MaxSan, Fights = run.Fights.Select(Clone).ToList(), Rewards = run.Rewards?.Select(Clone).ToList(), StorySave = Clone(run.StorySave), PassStageRecords = new(run.PassStageRecords), PassDiffRecords = new(run.PassDiffRecords) };
        return true;
    }

    private static Theatre6EndGameResponse BuildEndResponse(Theatre6SettlementState x) => new() { Code = 0, StoryModeSaveDb = ToWire(x.StorySave), SettleData = new Theatre6SettleData { IsWin = x.IsWin, FileData = ToWire(x.File), CurHeath = x.CurHealth, MaxHeath = x.MaxHealth, CurSan = x.CurSan, MaxSan = x.MaxSan, FightRecords = x.Fights.Select(y => new Theatre6FightRecord { DifficultyType=y.DifficultyType,FightResultType=y.FightResultType,FightId=y.FightId,MonsterId=y.MonsterId }).ToList(), RewardList = x.Rewards?.Select(y => new Theatre6SettleReward { Id=y.Id,Type=y.Type,Count=y.Count,IsFirst=y.IsFirst }).ToList(), PassStageRecords = new(x.PassStageRecords), PassDiffRecords = new(x.PassDiffRecords) } };
    private static Theatre6StoryModeSaveDb ToWire(Theatre6StorySaveState x) => new() { StoryIds=x.StoryIds.ToList(), StoryLineDatas=x.StoryLineDatas.Select(y => new Theatre6StoryLineData { StoryLineId=y.StoryLineId,StageIndex=y.StageIndex,IsCompletedBefore=y.IsCompletedBefore,IsBuy=y.IsBuy,BuyIndex=y.BuyIndex.ToList() }).ToList() };
    internal static Theatre6FileData ToWire(Theatre6FileState x) => new() { SlotId=x.SlotId,CharacterId=x.CharacterId,Score=x.Score,BuildTags=x.BuildTags.ToList(),FashionId=x.FashionId,Attrs=x.Attrs.Select(y=>new Theatre6AttrData{AttrId=y.AttrId,Value=y.Value}).ToList(),Skills=x.Skills.Select(y=>new Theatre6SkillData{SlotType=y.SlotType,Position=y.Position,SkillId=y.SkillId}).ToList(),AttrPacks=x.AttrPacks.Select(y=>new Theatre6AttrPackData{PackId=y.PackId,Num=y.Num}).ToList(),Buffs=x.Buffs.Select(y=>new Theatre6BuffData{BuffId=y.BuffId,TriggerCount=y.TriggerCount,AddMagic=y.AddMagic}).ToList() };
    internal static Theatre6FileState Clone(Theatre6FileState x) => new() { SlotId=x.SlotId,CharacterId=x.CharacterId,Score=x.Score,BuildTags=x.BuildTags.ToList(),FashionId=x.FashionId,Attrs=x.Attrs.Select(y=>new Theatre6AttrState{AttrId=y.AttrId,Value=y.Value}).ToList(),Skills=x.Skills.Select(y=>new Theatre6SkillState{SlotType=y.SlotType,Position=y.Position,SkillId=y.SkillId}).ToList(),AttrPacks=x.AttrPacks.Select(y=>new Theatre6AttrPackState{PackId=y.PackId,Num=y.Num}).ToList(),Buffs=x.Buffs.Select(y=>new Theatre6BuffState{BuffId=y.BuffId,TriggerCount=y.TriggerCount,AddMagic=y.AddMagic}).ToList() };
    private static Theatre6StorySaveState Clone(Theatre6StorySaveState x) => new() { StoryIds=x.StoryIds.ToList(), StoryLineDatas=x.StoryLineDatas.Select(y=>new Theatre6StoryLineState{StoryLineId=y.StoryLineId,StageIndex=y.StageIndex,IsCompletedBefore=y.IsCompletedBefore,IsBuy=y.IsBuy,BuyIndex=y.BuyIndex.ToList()}).ToList() };
    private static Theatre6FightRecordState Clone(Theatre6FightRecordState x) => new() { DifficultyType=x.DifficultyType,FightResultType=x.FightResultType,FightId=x.FightId,MonsterId=x.MonsterId };
    private static Theatre6SettleRewardState Clone(Theatre6SettleRewardState x) => new() { Id=x.Id,Type=x.Type,Count=x.Count,IsFirst=x.IsFirst };
    private static void MergeMax(Dictionary<int,int> target, Dictionary<int,int> source) { foreach ((int key,int value) in source) target[key] = target.TryGetValue(key,out int old) ? Math.Max(old,value) : value; }
}
