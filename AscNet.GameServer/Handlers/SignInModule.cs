using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.signin;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    internal class SignInModule
    {
        // ponytail: cold-loaded once at first use; O(n) per-sign lookups are fine at
        // sign-in table scale. Promote to dictionaries keyed per round/day if it grows.
        private static readonly Lazy<IReadOnlyDictionary<int, SignInTable>> SignsById = new(() =>
            TableReaderV2.Parse<SignInTable>().ToDictionary(row => row.Id, row => row));
        private static readonly Lazy<IReadOnlyDictionary<(int SignId, int Round, int Day), SignInRewardTable>> RewardsBySignRoundDay = new(() =>
            TableReaderV2.Parse<SignInRewardTable>()
                .ToDictionary(row => (row.SignId, row.Round, row.Day), row => row));

        /// <summary>Generic sign-in rejections carry a non-zero code; the exact retail codes are unobserved.</summary>
        private const int SignInErrorCode = 1;
        private const int BusinessDayOffsetSeconds = 5 * 3600; // daily claim boundary is 05:00 UTC


        [RequestPacketHandler("SignInRequest")]
        public static void SignInRequestHandler(Session session, Packet.Request packet)
        {
            SignInRequest request = packet.Deserialize<SignInRequest>();
            SignInResponse response = ProcessSignInRequest(session, request.Id, DateTimeOffset.UtcNow);
            session.SendResponse(response, packet.Id);
        }

        /// <summary>Table-driven generic claim for both Type 1 daily and Type 2 scheduled event sign-ins.</summary>
        internal static SignInResponse ProcessSignInRequest(Session session, int signId, DateTimeOffset now)
        {
            SignInResponse response = new();
            if (!SignsById.Value.TryGetValue(signId, out SignInTable? sign))
                return Reject(response);

            if (!IsSignInOpen(sign, session.player, now))
                return Reject(response);

            // Same-day duplicate: idempotent no-op (no reward, no state mutation), matching retail.
            if (HasSignedToday(session.player, signId, now))
                return response;

            if (IsSignInComplete(sign, session.player, signId))
                return Reject(response);

            if (!TryGetCurrentReward(sign, session.player, signId, out SignInRewardTable? reward)
                || reward is null)
                return Reject(response);

            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(reward.RewardId);
            if (goods.Count == 0)
                return Reject(response);

            GetSignProgress(sign, session.player, signId, got: false, out int round, out int day);
            string claimKey = $"signin:{signId}:{round}:{day}";
            bool alreadyGranted = ClaimKeyAlreadyApplied(session, claimKey);
            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(claimKey, goods)], session);
            result.SendPushes(session);

            PlayerSignInState state = GetOrCreateSignInState(session.player, signId);
            if (!alreadyGranted)
                state.ClaimCount++;
            state.LastSignInTime = now.ToUnixTimeSeconds();
            session.player.Save();

            response.RewardGoodsList.AddRange(result.RewardGoods);
            return response;
        }

        internal static List<SignInfo> BuildLoginSignInfos(Player player)
            => BuildLoginSignInfos(player, DateTimeOffset.UtcNow);

        internal static List<SignInfo> BuildLoginSignInfos(Player player, DateTimeOffset now)
        {
            List<SignInfo> infos = [];
            foreach (SignInTable sign in SignsById.Value.Values.OrderBy(sign => sign.Id))
            {
                if (!IsSignInOpen(sign, player, now))
                    continue;

                bool got = HasSignedToday(player, sign.Id, now) || IsSignInComplete(sign, player, sign.Id);
                GetSignProgress(sign, player, sign.Id, got, out int round, out int day);
                infos.Add(new SignInfo
                {
                    Id = sign.Id,
                    Round = round,
                    Day = day,
                    Got = got,
                    FinishDay = 0
                });
            }
            return infos;
        }

        /// <summary>Full <see cref="NotifySignInData"/> replacement for a 05:00 transition/login reconciliation.</summary>
        public static NotifySignInData BuildNotifySignInData(Player player, DateTimeOffset now)
            => new() { SignInfos = BuildLoginSignInfos(player, now) };

        /// <summary>Sends the full sign-in replacement push; callers drive the reconciliation, not a timer.</summary>
        public static void SendSignInResetPush(Session session, DateTimeOffset now)
            => session.SendPush(BuildNotifySignInData(session.player, now));

        private static SignInResponse Reject(SignInResponse response)
        {
            response.Code = SignInErrorCode;
            return response;
        }

        /// <summary>Type 1 daily is always open; Type 2 events require an open schedule window and level gate.</summary>
        private static bool IsSignInOpen(SignInTable sign, Player player, DateTimeOffset now)
        {
            if (sign.Type == 1)
                return true;
            if (sign.TimeId is not int timeId || timeId <= 0 || !ActivityScheduleService.IsOpen(timeId, now))
                return false;
            return sign.OpenLevel is not int requiredLevel || player.PlayerData.Level >= requiredLevel;
        }

        /// <summary>Type 2 events complete after every configured day is claimed; the daily log-in recurs.</summary>
        private static bool IsSignInComplete(SignInTable sign, Player player, int signId)
        {
            if (sign.Type != 2)
                return false;
            return GetOrCreateSignInState(player, signId).ClaimCount >= TotalDays(sign);
        }

        private static int TotalDays(SignInTable sign)
            => sign.RoundDays.Count > 0 ? sign.RoundDays.Sum() : 1;
        /// <summary>Wire Round/Day. Events never roll past their last day; Got=false surfaces the next claimable day.</summary>
        private static void GetSignProgress(SignInTable sign, Player player, int signId, bool got, out int round, out int day)
        {
            long claims = GetOrCreateSignInState(player, signId).ClaimCount;
            int roundDays = TotalDays(sign);
            if (sign.Type == 2)
            {
                round = 1;
                day = (int)Math.Min(got ? claims : claims + 1, roundDays);
            }
            else
            {
                long displayed = got && claims > 0 ? claims - 1 : claims;
                round = (int)(displayed / roundDays) + 1;
                day = (int)(displayed % roundDays) + 1;
            }
        }

        private static bool TryGetCurrentReward(
            SignInTable sign,
            Player player,
            int signId,
            out SignInRewardTable? reward)
        {
            GetSignProgress(sign, player, signId, got: false, out int round, out int day);
            return RewardsBySignRoundDay.Value.TryGetValue((signId, round, day), out reward);
        }

        private static bool HasSignedToday(Player player, int signId, DateTimeOffset now)
        {
            long lastSignInTime = GetOrCreateSignInState(player, signId).LastSignInTime;
            return lastSignInTime > 0
                && (lastSignInTime - BusinessDayOffsetSeconds) / 86_400
                    == (now.ToUnixTimeSeconds() - BusinessDayOffsetSeconds) / 86_400;
        }

        private static PlayerSignInState GetOrCreateSignInState(Player player, int signId)
        {
            player.SignInStates ??= [];
            PlayerSignInState? state = player.SignInStates.FirstOrDefault(candidate => candidate.Id == signId);
            if (state is not null)
                return state;

            state = new PlayerSignInState { Id = signId };
            player.SignInStates.Add(state);

            // Normalize the legacy global daily counters into the Id 1 state exactly once;
            // from here on per-sign state is the only write path (no dual-write aliases).
            if (signId == 1 && (player.SignInClaimCount > 0 || player.LastSignInTime > 0))
            {
                state.ClaimCount = Math.Max(player.SignInClaimCount, 0);
                state.LastSignInTime = player.LastSignInTime;
            }
            return state;
        }

        private static bool ClaimKeyAlreadyApplied(Session session, string claimKey)
        {
            return session.inventory.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal)
                || session.character.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal);
        }
    }
}
