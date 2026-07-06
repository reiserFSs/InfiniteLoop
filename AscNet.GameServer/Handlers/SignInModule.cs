﻿using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    internal class SignInModule
    {
        private const int CurrentSignInId = 1;
        private const int CurrentSignInRewardRecordId = 50210;
        private const int CurrentSignInRewardItemId = Inventory.Coin;
        private const int CurrentSignInRewardCount = 10_000;

        [RequestPacketHandler("SignInRequest")]
        public static void SignInRequestHandler(Session session, Packet.Request packet)
        {
            SignInRequest request = MessagePackSerializer.Deserialize<SignInRequest>(packet.Content);
            SignInResponse signInResponse = new();

            if (request.Id == CurrentSignInId && !HasSignedInToday(session.player))
            {
                RewardGoods rewardGoods = BuildDailySignInReward();
                signInResponse.RewardGoodsList.Add(rewardGoods);
                RewardHandler.GiveRewards(new[]
                {
                    new Reward
                    {
                        Id = rewardGoods.TemplateId,
                        Count = rewardGoods.Count,
                        Level = rewardGoods.Level,
                        Type = (RewardType)rewardGoods.RewardType
                    }
                }, session);

                session.player.LastSignInTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                session.inventory.Save();
                session.player.Save();
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
                new() { Id = CurrentSignInId, Round = 1, Day = 22, Got = signedToday, FinishDay = signedToday ? 1787 : 1786 },
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

        private static RewardGoods BuildDailySignInReward()
        {
            return new()
            {
                RewardType = (int)RewardType.Item,
                TemplateId = CurrentSignInRewardItemId,
                Count = CurrentSignInRewardCount,
                Level = 0,
                Quality = 0,
                Grade = 0,
                Breakthrough = 0,
                ConvertFrom = 0,
                Id = CurrentSignInRewardRecordId,
                IsGift = false,
                RewardMulti = 0
            };
        }
    }
}
