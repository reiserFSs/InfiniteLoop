using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.guildsign;
using System.Globalization;

namespace AscNet.GameServer.Handlers;

internal partial class GuildModule
{
    private static readonly Lazy<GuildSignTable[]> SignOutcomes = new(() => TableReaderV2.Parse<GuildSignTable>().ToArray());
    private static readonly Lazy<GuildSignEventTable[]> SignEvents = new(() => TableReaderV2.Parse<GuildSignEventTable>().ToArray());
    private static readonly Lazy<GuildSignSpecialDateSignTable[]> SignSpecialDates = new(() => TableReaderV2.Parse<GuildSignSpecialDateSignTable>().ToArray());

    [RequestPacketHandler("GuildSignRequest")]
    public static void GuildSignRequestHandler(Session session, Packet.Request packet)
    {
        Handle<GuildEmptyRequest, GuildSignResponse>(session, packet, (mutation, _, response) =>
        {
            long uid = session.player.PlayerData.Id;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            PrepareSign(mutation, uid, now);
            GuildPlayerState state = mutation.Player(uid);
            Require(state.SignInfo.Id == 0, 20063342); // GuildSignAlreadySign

            int minimum = SignConfigInt("GuildSignGuaranteeMin");
            int maximum = SignConfigInt("GuildSignGuaranteeMax");
            int guaranteeId = SignConfigInt("GuildSignGuaranteeId");
            if (minimum <= 0 || maximum < minimum)
                throw new InvalidOperationException("Invalid Guild sign guarantee range.");
            if (state.SignGuaranteeThreshold == 0)
                state.SignGuaranteeThreshold = checked((int)Random.Shared.NextInt64(minimum, (long)maximum + 1));

            DateTimeOffset gameDate = GameDate(now);
            // Local policy: literal holiday > birthday > ordinary guarantee > weighted ordinary.
            // Special dates include their authored year; holidays never recur implicitly.
            GuildSignSpecialDateSignTable? special = SignSpecialDates.Value.FirstOrDefault(row =>
                DateTime.Parse(row.Date, CultureInfo.InvariantCulture).Date == gameDate.Date);
            Birthday? birthday = session.player.PlayerData.Birthday;
            GuildSignTable outcome;
            if (special is not null)
                outcome = SignOutcomes.Value.Single(row => row.Id == special.SignId);
            else if (birthday is not null && birthday.Mon == gameDate.Month && birthday.Day == gameDate.Day)
                outcome = SignOutcomes.Value.Single(row => row.Type == 2);
            else
            {
                state.SignGuaranteeCount = checked(state.SignGuaranteeCount + 1);
                outcome = state.SignGuaranteeCount >= state.SignGuaranteeThreshold
                    ? SignOutcomes.Value.Single(row => row.Id == guaranteeId)
                    : WeightedSignChoice(SignOutcomes.Value.Where(row => row.Type == 1).ToArray(), row => row.Weight ?? 0);
            }

            if (outcome.SignType.Count != outcome.SignNum.Count || outcome.RewardId.Count != outcome.RewardWeight.Count)
                throw new InvalidOperationException("Invalid Guild sign event/reward configuration.");
            List<int> eventIds = [];
            for (int i = 0; i < outcome.SignType.Count; i++)
            {
                int[] candidates = SignEvents.Value.Where(row => row.SignType == outcome.SignType[i]).Select(row => row.Id).ToArray();
                int count = outcome.SignNum[i];
                if (count < 0 || count > candidates.Length)
                    throw new InvalidOperationException("Guild sign event count exceeds its authored pool.");
                // Sample without replacement so the displayed fortune never repeats an event.
                for (int j = 0; j < count; j++)
                {
                    int selected = Random.Shared.Next(j, candidates.Length);
                    (candidates[j], candidates[selected]) = (candidates[selected], candidates[j]);
                    eventIds.Add(candidates[j]);
                }
            }
            int rewardIndex = WeightedSignChoice(Enumerable.Range(0, outcome.RewardId.Count).ToArray(), index => outcome.RewardWeight[index]);
            state.SignInfo = new GuildSignInfo
            {
                Id = outcome.Id,
                SignEventIds = eventIds,
                // Preview only: the core freezes this outcome before any inventory writes.
                RewardGoodsList = mutation.ResolveReward(outcome.RewardId[rewardIndex])
            };
            if (outcome.Id == guaranteeId)
            {
                state.SignGuaranteeCount = 0;
                state.SignGuaranteeThreshold = 0;
            }
            response.GuildSignInfo = state.SignInfo;
            mutation.Push(uid, new NotifyGuildSignPlayerData { GuildSignInfo = state.SignInfo });
        }, membershipCode: 20063345); // GuildSignPlayerNotInGuild, including tourists
    }

    [RequestPacketHandler("GuildSignRewardRequest")]
    public static void GuildSignRewardRequestHandler(Session session, Packet.Request packet)
    {
        Handle<GuildEmptyRequest, GuildSignRewardResponse>(session, packet, (mutation, _, response) =>
        {
            long uid = session.player.PlayerData.Id;
            PrepareSign(mutation, uid);
            GuildSignInfo info = mutation.Player(uid).SignInfo;
            Require(info.Id != 0, 20063343); // GuildSignNotSign
            Require(!info.IsAward, 20063344); // GuildSignAwardIsGet
            mutation.AddRewardGoods(uid, info.RewardGoodsList);
            info.IsAward = true;
            response.RewardGoodsList = info.RewardGoodsList;
            mutation.Push(uid, new NotifyGuildSignPlayerData { GuildSignInfo = info });
        }, membershipCode: 20063345);
    }

    internal static void PrepareSign(GuildMutation mutation, long uid) => PrepareSign(mutation, uid, DateTimeOffset.UtcNow);

    private static void PrepareSign(GuildMutation mutation, long uid, DateTimeOffset now)
    {
        GuildPlayerState state = mutation.Player(uid);
        long period = DailyPeriod(now);
        if (state.SignPeriod == period)
            return;
        state.SignPeriod = period;
        state.SignInfo = new GuildSignInfo();
    }

    internal static NotifyGuildSignPlayerData BuildSignLoginData(Session session)
    {
        Guild? guild = FindMembership(session.player.PlayerData.Id);
        return new NotifyGuildSignPlayerData
        {
            GuildSignInfo = guild is not null && Rank(guild, session.player.PlayerData.Id) != 5
                && session.player.GuildState.SignPeriod == DailyPeriod(DateTimeOffset.UtcNow)
                ? session.player.GuildState.SignInfo : new GuildSignInfo()
        };
    }

    private static int SignConfigInt(string key) => int.Parse(
        TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == key).Value!, CultureInfo.InvariantCulture);

    private static T WeightedSignChoice<T>(IReadOnlyList<T> candidates, Func<T, int> weight)
    {
        long total = 0;
        foreach (T candidate in candidates)
        {
            int value = weight(candidate);
            if (value < 0)
                throw new InvalidOperationException("Guild sign weight cannot be negative.");
            total = checked(total + value);
        }
        if (total == 0)
            throw new InvalidOperationException("Guild sign pool has no positive weights.");
        long selected = Random.Shared.NextInt64(total);
        foreach (T candidate in candidates)
        {
            selected -= weight(candidate);
            if (selected < 0)
                return candidate;
        }
        throw new InvalidOperationException("Guild sign weight selection failed.");
    }
}
