using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.signin;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    internal class SignInModule
    {
        private static readonly Lazy<SignInTable> DailySignIn = new(() => TableReaderV2.Parse<SignInTable>()
            .Single(row => row.Type == 1));
        private static readonly Lazy<Dictionary<int, SignInRewardTable>> DailyRewardsByDay = new(() => TableReaderV2.Parse<SignInRewardTable>()
            .Where(row => row.SignId == DailySignIn.Value.Id)
            .ToDictionary(row => row.Day, row => row));


        [RequestPacketHandler("SignInRequest")]
        public static void SignInRequestHandler(Session session, Packet.Request packet)
        {
            SignInRequest request = MessagePackSerializer.Deserialize<SignInRequest>(packet.Content);
            SignInResponse signInResponse = new();

            if (request.Id == DailySignIn.Value.Id && !HasSignedInToday(session.player))
            {
                SignInRewardTable? reward = GetCurrentSignInReward(session.player);
                List<RewardGoods> rewardGoods = reward is null
                    ? []
                    : RewardHandler.GiveRewards(RewardHandler.GetRewardGoods(reward.RewardId), session);
                if (rewardGoods.Count > 0)
                {
                    signInResponse.RewardGoodsList.AddRange(rewardGoods);
                    session.player.SignInClaimCount = Math.Max(session.player.SignInClaimCount, 0) + 1;
                    session.player.LastSignInTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    session.inventory.Save();
                    session.character.Save();
                    session.player.Save();
                }
                else
                {
                    session.log.Error($"No reward is configured for daily sign-in day {GetCurrentSignInDay(session.player, signedToday: false)}.");
                }
            }

            session.SendResponse(signInResponse, packet.Id);
        }

        internal static List<SignInfo> BuildLoginSignInfos(Player player)
        {
            bool signedToday = HasSignedInToday(player);
            return
            [
                new()
                {
                    Id = DailySignIn.Value.Id,
                    Round = GetCurrentSignInRound(player, signedToday),
                    Day = GetCurrentSignInDay(player, signedToday),
                    Got = signedToday,
                    FinishDay = 0
                },
            ];
        }

        internal static bool HasSignedInToday(Player player)
        {
            if (player.LastSignInTime <= 0)
                return false;

            DateTime lastSignInDate = DateTimeOffset.FromUnixTimeSeconds(player.LastSignInTime).UtcDateTime.Date;
            return lastSignInDate == DateTimeOffset.UtcNow.UtcDateTime.Date;
        }

        private static SignInRewardTable? GetCurrentSignInReward(Player player)
        {
            DailyRewardsByDay.Value.TryGetValue((int)GetCurrentSignInDay(player, signedToday: false), out SignInRewardTable? reward);
            return reward;
        }

        private static long GetCurrentSignInRound(Player player, bool signedToday)
        {
            return GetDisplayedSignInClaimCount(player, signedToday) / DailySignIn.Value.RoundDays + 1;
        }

        private static long GetCurrentSignInDay(Player player, bool signedToday)
        {
            return GetDisplayedSignInClaimCount(player, signedToday) % DailySignIn.Value.RoundDays + 1;
        }

        private static long GetDisplayedSignInClaimCount(Player player, bool signedToday)
        {
            long completedClaims = Math.Max(player.SignInClaimCount, 0);
            return signedToday && completedClaims > 0 ? completedClaims - 1 : completedClaims;
        }
    }
}
