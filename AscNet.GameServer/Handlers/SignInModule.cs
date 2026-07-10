using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    internal class SignInModule
    {
        private const int CurrentSignInId = 1;
        private const int FirstSignInRewardId = 5000;
        private const int SignInDaysPerRound = 28;
        // The active schedule expiry is configuration-owned; no local source provides it.
        private const int UnspecifiedSignInFinishDay = 0;


        [RequestPacketHandler("SignInRequest")]
        public static void SignInRequestHandler(Session session, Packet.Request packet)
        {
            SignInRequest request = MessagePackSerializer.Deserialize<SignInRequest>(packet.Content);
            SignInResponse signInResponse = new();

            if (request.Id == CurrentSignInId && !HasSignedInToday(session.player))
            {
                List<RewardGoods> rewardGoods = RewardHandler.GiveRewards(
                    RewardHandler.GetRewardGoods(GetCurrentSignInRewardId(session.player)),
                    session);
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
                    session.log.Error($"No rewards configured for daily sign-in day {GetCurrentSignInDay(session.player, signedToday: false)}.");
                }
            }

            session.SendResponse(signInResponse, packet.Id);
        }

        internal static List<SignInfo> BuildLoginSignInfos(Player player)
        {
            bool signedToday = HasSignedInToday(player);
            return
            [
                new() { Id = 2, Round = 2, Day = 7, Got = true, FinishDay = 1002 },
                new() { Id = 42, Round = 1, Day = 7, Got = true, FinishDay = 996 },
                new()
                {
                    Id = CurrentSignInId,
                    Round = GetCurrentSignInRound(player, signedToday),
                    Day = GetCurrentSignInDay(player, signedToday),
                    Got = signedToday,
                    FinishDay = UnspecifiedSignInFinishDay
                },
                new() { Id = 76, Round = 1, Day = 1, Got = false, FinishDay = 0 },
                new() { Id = 87, Round = 1, Day = 1, Got = false, FinishDay = 0 },
                new() { Id = 93, Round = 1, Day = 1, Got = false, FinishDay = 0 },
                new() { Id = 98, Round = 1, Day = 1, Got = false, FinishDay = 0 },
                new() { Id = 106, Round = 1, Day = 1, Got = false, FinishDay = 0 },
                new() { Id = 113, Round = 1, Day = 1, Got = false, FinishDay = 0 },
                new() { Id = 111, Round = 1, Day = 7, Got = true, FinishDay = 1792 }
            ];
        }

        internal static bool HasSignedInToday(Player player)
        {
            if (player.LastSignInTime <= 0)
                return false;

            DateTime lastSignInDate = DateTimeOffset.FromUnixTimeSeconds(player.LastSignInTime).UtcDateTime.Date;
            return lastSignInDate == DateTimeOffset.UtcNow.UtcDateTime.Date;
        }

        private static int GetCurrentSignInRewardId(Player player)
        {
            return FirstSignInRewardId + (int)GetCurrentSignInDay(player, signedToday: false) - 1;
        }

        private static long GetCurrentSignInRound(Player player, bool signedToday)
        {
            return GetDisplayedSignInClaimCount(player, signedToday) / SignInDaysPerRound + 1;
        }

        private static long GetCurrentSignInDay(Player player, bool signedToday)
        {
            return GetDisplayedSignInClaimCount(player, signedToday) % SignInDaysPerRound + 1;
        }

        private static long GetDisplayedSignInClaimCount(Player player, bool signedToday)
        {
            long completedClaims = Math.Max(player.SignInClaimCount, 0);
            return signedToday && completedClaims > 0 ? completedClaims - 1 : completedClaims;
        }
    }
}
