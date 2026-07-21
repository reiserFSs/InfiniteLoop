using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.dormitory.character;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers;

#pragma warning disable CS8618
[MessagePackObject(true)] public sealed class GetFondleDataRequest { public uint CharacterId; }
[MessagePackObject(true)] public sealed class GetFondleDataResponse { public int Code; public uint LastRecoveryTime; public int FondleCount; }
[MessagePackObject(true)] public sealed class DormDoFondleRequest { public uint CharacterId; public int FondleType; }
[MessagePackObject(true)] public sealed class DormDoFondleResponse { public int Code; }
[MessagePackObject(true)] public sealed class NotifyCharacterMood { public uint CharacterId; public int Mood; }
[MessagePackObject(true)] public sealed class DormCharacterAttr { public uint CharacterId; public int Mood; public int Vitality; }
[MessagePackObject(true)] public sealed class NotifyCharacterAttr { public List<DormCharacterAttr> AttrList = []; }
[MessagePackObject(true)] public sealed class DormCharacterOperateRequest { public uint CharacterId; public int EventId; public int OperateType; }
[MessagePackObject(true)] public sealed class DormCharacterOperateResponse { public int Code; public int MoodValue; public List<RewardGoods> RewardGoods = []; }
[MessagePackObject(true)] public sealed class DormWordDoneRequest { public List<int> WorkPos = []; }
[MessagePackObject(true)] public sealed class DormWordDoneResponse { public int Code; public List<DormWorkReward> WorkRewards = []; public List<RewardGoods> ExtraRewards = []; }
#pragma warning restore CS8618

internal partial class DormModule
{
    private const int InvalidCharacter = 20060009;
    private const int InvalidFondleCharacter = 20060058;
    private const int InvalidFondleType = 20060059;
    private const int EventTemplateMissing = 20060010;
    private const int EventAbsent = 20060012;
    private const int NotWorking = 20060027;
    private const int MoodLow = 20060045;

    [RequestPacketHandler("GetFondleDataRequest")]
    public static void GetFondleDataRequestHandler(Session session, Packet.Request packet)
    {
        GetFondleDataRequest request = packet.Deserialize<GetFondleDataRequest>();
        PlayerDormCharacter? character = session.player.Dorm.Characters.FirstOrDefault(x => x.CharacterId == request.CharacterId);
        DormCharacterFondleTable? row = TableReaderV2.Parse<DormCharacterFondleTable>().FirstOrDefault(x => x.CharacterId == request.CharacterId);
        if (character is null || row is null)
        {
            session.SendResponse(new GetFondleDataResponse { Code = InvalidFondleCharacter }, packet.Id);
            return;
        }
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool changed = NormalizeFondle(character, row, now);
        if (changed) session.player.Save();
        session.SendResponse(new GetFondleDataResponse { LastRecoveryTime = character.LastFondleRecoveryTime, FondleCount = character.LeftFondleCount }, packet.Id);
    }

    [RequestPacketHandler("DormDoFondleRequest")]
    public static void DormDoFondleRequestHandler(Session session, Packet.Request packet)
    {
        DormDoFondleRequest request = packet.Deserialize<DormDoFondleRequest>();
        PlayerDormCharacter? character = session.player.Dorm.Characters.FirstOrDefault(x => x.CharacterId == request.CharacterId);
        DormCharacterFondleTable? row = TableReaderV2.Parse<DormCharacterFondleTable>().FirstOrDefault(x => x.CharacterId == request.CharacterId);
        if (character is null || row is null)
        {
            session.SendResponse(new DormDoFondleResponse { Code = InvalidFondleCharacter }, packet.Id);
            return;
        }
        if (request.FondleType is < 1 or > 3)
        {
            session.SendResponse(new DormDoFondleResponse { Code = InvalidFondleType }, packet.Id);
            return;
        }
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        NormalizeFondle(character, row, now);
        if (character.LeftFondleCount <= 0)
        {
            session.SendResponse(new DormDoFondleResponse { Code = DormRequestDataInvalid }, packet.Id);
            return;
        }
        if (character.LeftFondleCount == row.MaxCount) character.LastFondleRecoveryTime = now;
        character.LeftFondleCount--;
        int lower = row.Lower[request.FondleType - 1];
        int upper = row.Upper[request.FondleType - 1];
        character.Mood = Math.Clamp(character.Mood + Random.Shared.Next(lower, checked(upper + 1)), 0, 10000);
        session.player.Save();
        TaskModule.RecordTableDrivenProgress(session, [(29006, null, 1)]);
        session.SendPush(new NotifyCharacterMood { CharacterId = character.CharacterId, Mood = character.Mood / 100 });
        session.SendResponse(new DormDoFondleResponse(), packet.Id);
    }

    [RequestPacketHandler("DormCharacterOperateRequest")]
    public static void DormCharacterOperateRequestHandler(Session session, Packet.Request packet) =>
        session.SendResponse(new DormCharacterOperateResponse { Code = DormRequestDataInvalid }, packet.Id);

    [RequestPacketHandler("DormWordDoneRequest")]
    public static void DormWordDoneRequestHandler(Session session, Packet.Request packet)
    {
        DormWordDoneRequest request = packet.Deserialize<DormWordDoneRequest>();
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Dictionary<int, DormCharacterWorkTable> rows = TableReaderV2.Parse<DormCharacterWorkTable>().ToDictionary(x => x.DormitoryNum);
        List<PlayerDormWork> work = request.WorkPos.Distinct().Count() == request.WorkPos.Count
            ? request.WorkPos.Select(pos => session.player.Dorm.WorkList.FirstOrDefault(x => x.WorkPos == pos)).OfType<PlayerDormWork>().ToList() : new List<PlayerDormWork>();
        List<PlayerDormCharacter> characters = work.Select(x => session.player.Dorm.Characters.FirstOrDefault(character => character.CharacterId == x.CharacterId)).OfType<PlayerDormCharacter>().ToList();
        foreach (PlayerDormWork entry in work.Where(entry => entry.WorkEndTime > now && string.IsNullOrEmpty(entry.ClaimKey)))
            entry.ClaimKey = Guid.NewGuid().ToString("N");
        bool valid = work.Count == request.WorkPos.Count && characters.Count == work.Count
            && work.All(entry => rows.ContainsKey(entry.DormitoryNum)
                && (entry.WorkEndTime > now || entry.WorkEndTime == 0 && session.player.Dorm.PendingRewards.Any(
                    pending => pending.Key == WorkClaimKey(session, entry)))
                && (entry.WorkEndTime == 0 || characters.First(character => character.CharacterId == entry.CharacterId).Mood >= rows[entry.DormitoryNum].Mood));
        if (!valid)
        {
            session.SendResponse(new DormWordDoneResponse { Code = NotWorking }, packet.Id);
            return;
        }
        int multiplier = Config().GetValueOrDefault("DormWorkRewardPerVitality");
        List<DormWorkReward> rewards = work.Select(x => new DormWorkReward { WorkPos = x.WorkPos, ItemId = rows[x.DormitoryNum].ItemId, ItemNum = checked(x.RewardNum * multiplier), ResetCount = x.ResetCount }).ToList();
        List<PlayerDormPendingReward> pending = work.Select((entry, index) =>
            session.player.Dorm.PendingRewards.FirstOrDefault(value => value.Key == WorkClaimKey(session, entry))
            ?? new PlayerDormPendingReward
            {
                Key = WorkClaimKey(session, entry),
                Goods = new[] { new PlayerDormPendingRewardItem { TemplateId = rewards[index].ItemId, Count = rewards[index].ItemNum } }
                    .Concat(RewardHandler.GetRewardGoods(rows[entry.DormitoryNum].ExtraReward).Select(item =>
                        new PlayerDormPendingRewardItem { Id = item.Id, TemplateId = item.TemplateId, Count = item.Count, Params = item.Params.ToList() })).ToList()
            }).ToList();
        List<PlayerDormWork> completed = work.Where(entry => entry.WorkEndTime > 0).ToList();
        Dictionary<PlayerDormWork, uint> previousEndTimes = completed.ToDictionary(entry => entry, entry => entry.WorkEndTime);
        Dictionary<PlayerDormCharacter, int> previousMood = completed.ToDictionary(
            entry => characters.First(character => character.CharacterId == entry.CharacterId),
            entry => characters.First(character => character.CharacterId == entry.CharacterId).Mood);
        foreach (PlayerDormWork entry in completed)
        {
            characters.First(character => character.CharacterId == entry.CharacterId).Mood -= rows[entry.DormitoryNum].Mood;
            entry.WorkEndTime = 0;
        }
        foreach (PlayerDormPendingReward entry in pending.Where(entry => !session.player.Dorm.PendingRewards.Contains(entry)))
            session.player.Dorm.PendingRewards.Add(entry);
        if (completed.Count > 0)
        {
            try { session.player.SaveChecked(); }
            catch
            {
                foreach ((PlayerDormWork entry, uint endTime) in previousEndTimes) entry.WorkEndTime = endTime;
                foreach ((PlayerDormCharacter character, int mood) in previousMood) character.Mood = mood;
                session.player.Dorm.PendingRewards.RemoveAll(entry => pending.Contains(entry));
                session.SendResponse(new DormWordDoneResponse { Code = NotWorking }, packet.Id);
                return;
            }
        }
        RewardApplicationResult grant;
        try
        {
            grant = RewardHandler.ApplyRewardsOnceAndPersist(pending.Select(entry => new RewardGrant(entry.Key,
                entry.Goods.Select(item => new RewardGoodsTable { Id = item.Id, TemplateId = item.TemplateId, Count = item.Count, Params = item.Params.ToList() }).ToList())).ToList(), session);
        }
        catch
        {
            session.SendResponse(new DormWordDoneResponse { Code = NotWorking }, packet.Id);
            return;
        }
        session.player.Dorm.PendingRewards.RemoveAll(entry => pending.Contains(entry));
        try { session.player.SaveChecked(); }
        catch
        {
            session.player.Dorm.PendingRewards.AddRange(pending.Where(entry => !session.player.Dorm.PendingRewards.Contains(entry)));
            session.SendResponse(new DormWordDoneResponse { Code = NotWorking }, packet.Id);
            return;
        }
        grant.SendPushes(session);
        session.SendResponse(new DormWordDoneResponse { WorkRewards = rewards, ExtraRewards = grant.RewardGoods.Skip(work.Count).ToList() }, packet.Id);
        SendCharacterAttrs(session);
    }

    private static bool NormalizeFondle(PlayerDormCharacter character, DormCharacterFondleTable? row, uint now)
    {
        if (row is null) return false;
        if (character.LeftFondleCount >= row.MaxCount) return false;
        uint periods = now > character.LastFondleRecoveryTime ? (now - character.LastFondleRecoveryTime) / (uint)row.RecoveryTime : 0;
        if (periods == 0) return false;
        character.LeftFondleCount = Math.Min(row.MaxCount, checked(character.LeftFondleCount + (int)periods));
        character.LastFondleRecoveryTime += checked(periods * (uint)row.RecoveryTime);
        return true;
    }
}
