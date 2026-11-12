using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Table.V2.share.dormitory.character;
using AscNet.Table.V2.share.reward;

namespace AscNet.GameServer.Handlers;

internal partial class DormModule
{
    private static readonly Lazy<Dictionary<int, DormCharacterEventTable>> EventTemplates = new(() =>
        TableReaderV2.Parse<DormCharacterEventTable>().ToDictionary(row => row.EventId));
    private static readonly Lazy<Dictionary<int, DormCharacterEventRewardTable>> EventRewards = new(() =>
        TableReaderV2.Parse<DormCharacterEventRewardTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, DormCharacterMoodBuffTable>> EventMoodBuffs = new(() =>
        TableReaderV2.Parse<DormCharacterMoodBuffTable>().ToDictionary(row => row.Id));

    private sealed record DormEventCompletion(PlayerDormCharacter Character, PlayerDormEvent Event,
        int Type, int Mood, List<RewardGoodsTable> Goods);

    private static bool EventIsActive(PlayerDormEvent evt, uint now) => evt.EndTime == 0 || evt.EndTime > now;

    private static DormEventCompletion? PrepareEvent(PlayerDormCharacter character, PlayerDormEvent evt, DormCharacterEventTable row)
    {
        if (!EventRewards.Value.TryGetValue(row.FinishReward, out var reward)
            || !EventMoodBuffs.Value.TryGetValue(reward.BuffId, out var buff) || buff.Type != 1 || buff.MaxCount != 1)
            return null;
        List<RewardGoodsTable> goods = reward.RewardId is > 0 ? RewardHandler.GetRewardGoods(reward.RewardId.Value) : [];
        return reward.RewardId is > 0 && goods.Count == 0 ? null : new(character, evt, row.Type, buff.Speed, goods);
    }

    private static (RewardApplicationResult Rewards, Dictionary<uint, int> MoodChange) CompleteEvents(
        Session session, List<DormEventCompletion> events, uint now, bool fondleResidents = false)
    {
        TaskModule.EnsureMissionResets(session);
        List<RewardGrant> grants = events.Where(entry => entry.Goods.Count > 0).Select(entry =>
            new RewardGrant($"dorm-event:{session.player.PlayerData.Id}:{entry.Character.CharacterId}:{entry.Event.EventId}:{entry.Event.EndTime}",
                entry.Goods)).ToList();
        RewardApplicationResult rewards = grants.Count == 0 ? new() : RewardHandler.ApplyRewardsOnceAndPersist(grants, session);

        Dictionary<int, int> previousCounters = new(session.player.MissionProgress.ConditionCounters);
        var fondles = fondleResidents
            ? TableReaderV2.Parse<DormCharacterFondleTable>().ToDictionary(row => (uint)row.CharacterId)
            : [];
        List<PlayerDormCharacter> residents = session.player.Dorm.Characters
            .Where(character => character.DormitoryId > 0 && fondles.ContainsKey(character.CharacterId)).ToList();
        var characters = events.Select(entry => entry.Character).Concat(residents).Distinct()
            .ToDictionary(character => character, character => (character.Mood, character.HighMoodSince,
                character.LeftFondleCount, character.LastFondleRecoveryTime, Events: character.EventList.ToList()));
        HashSet<PlayerDormCharacter> interacted = events.Select(entry => entry.Character).ToHashSet();
        try
        {
            int maxMood = Config()["DormMoodMaxValue"];
            foreach (DormEventCompletion entry in events)
            {
                entry.Character.Mood = Math.Clamp(entry.Character.Mood + entry.Mood, 0, maxMood);
                TrackResidentMood(entry.Character, now);
                entry.Character.EventList.Remove(entry.Event);
            }
            int fondled = 0;
            foreach (PlayerDormCharacter resident in residents)
            {
                DormCharacterFondleTable row = fondles[resident.CharacterId];
                NormalizeFondle(resident, row, now);
                if (resident.LeftFondleCount <= 0)
                    continue;
                ApplyFondle(resident, row, 1, now);
                interacted.Add(resident);
                fondled++;
            }
            TaskModule.ApplyTableDrivenProgressUnsaved(session,
                [(29005, null, events.Count), (29006, null, fondled), .. events.GroupBy(entry => entry.Type)
                    .Select(group => (29005, (int?)group.Key, group.Count()))], now);
            session.player.SaveChecked();
        }
        catch
        {
            session.player.MissionProgress.ConditionCounters = previousCounters;
            foreach (var (character, previous) in characters)
            {
                character.Mood = previous.Mood;
                character.HighMoodSince = previous.HighMoodSince;
                character.LeftFondleCount = previous.LeftFondleCount;
                character.LastFondleRecoveryTime = previous.LastFondleRecoveryTime;
                character.EventList = previous.Events;
            }
            throw;
        }
        session.DormTaskStateDirty = true;
        return (rewards, interacted.ToDictionary(character => character.CharacterId,
            character => character.Mood - characters[character].Mood));
    }

    private static bool EvaluateEvents(Session session, uint now)
    {
        int refresh = Config().GetValueOrDefault("DormEventRefreshTime");
        if (refresh <= 0)
            return false;

        bool changed = session.player.Dorm.Characters.Sum(character =>
            character.EventList.RemoveAll(evt => evt.EndTime > 0 && evt.EndTime <= now)) > 0;
        if (now < session.player.Dorm.EventNextRefreshTime)
            return changed;

        Dictionary<uint, List<DormCharacterEventTable>> events = EventTemplates.Value.Values
            .Where(row => row.CharacterId > 0 && row.EventId > 0 && row.Weight > 0
                && EventRewards.Value.ContainsKey(row.FinishReward))
            .GroupBy(row => (uint)row.CharacterId)
            .ToDictionary(group => group.Key, group => group.ToList());
        bool generated = false;
        foreach (PlayerDormCharacter character in session.player.Dorm.Characters)
        {

            if (character.DormitoryId <= 0 || character.EventList.Count > 0
                || session.player.Dorm.WorkList.Any(work => work.CharacterId == character.CharacterId && work.WorkEndTime > now))
                continue;
            if (!events.TryGetValue(character.CharacterId, out List<DormCharacterEventTable>? candidates))
                continue;
            int total = candidates.Sum(row => row.Weight);
            int roll = Random.Shared.Next(total);
            DormCharacterEventTable selected = candidates.First(row => (roll -= row.Weight) < 0);
            character.EventList.Add(new PlayerDormEvent { EventId = selected.EventId, EndTime = checked(now + (uint)refresh) });
            generated = changed = true;
        }
        if (generated)
            session.player.Dorm.EventNextRefreshTime = checked(now + (uint)refresh);
        return changed;
    }

    private static List<DormCharacterEvent> EventResponses(Session session) => session.player.Dorm.Characters.Select(character => new DormCharacterEvent
    {
        CharacterId = character.CharacterId,
        EventList = character.EventList.Select(evt => new DormEvent { EventId = evt.EventId, EndTime = evt.EndTime }).ToList()
    }).ToList();
}
