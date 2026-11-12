using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateCnFirstPayCompatibility()
    {
        using MongoCollectionOverride storage = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players, out _, out _);
        const long uid = 468703;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.ClientRegion = "cn";
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), "cn-first-pay");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int packetId = 468730;

        AssertEqual(40001014, Claim().Code, "CN first recharge requires a completed payment");
        player.TotalPayMoney = 99;
        AssertEqual(40001014, Claim().Code, "CN first recharge requires one yuan");
        player.TotalPayMoney = 0;

        int rechargeId = ++packetId;
        InvokeRegisteredRequestHandler(nameof(PayInitiatedRequest), harness.Session, rechargeId,
            new PayInitiatedRequest { Key = "Pay6" });
        _ = ReadPushPayload<NotifyAccumulatedPayData>(harness, nameof(NotifyAccumulatedPayData), "CN recharge data");
        _ = ReadPushPayload<NotifyAccumulatedPayMoney>(harness, nameof(NotifyAccumulatedPayMoney), "CN recharge amount");
        NotifyPayInfo recharge = ReadPushPayload<NotifyPayInfo>(harness, nameof(NotifyPayInfo), "CN first recharge amount");
        AssertEqual(0, ReadResponsePayload<PayInitiatedResponse>(harness, rechargeId, nameof(PayInitiatedResponse),
            "CN six-yuan recharge", maxPacketsToRead: 32).Code, "CN six-yuan recharge completes");
        AssertEqual(6f, recharge.TotalPayMoney, "CN first recharge notification uses yuan");
        AssertEqual(600L, players.LastReplacement!.TotalPayMoney, "CN recharge persists the paid amount in hundredths");

        int claimId = ++packetId;
        InvokeRegisteredRequestHandler(nameof(GetFirstPayRewardRequest), harness.Session, claimId, new GetFirstPayRewardRequest());
        NotifyPayInfo claimed = ReadPushPayload<NotifyPayInfo>(harness, nameof(NotifyPayInfo), "CN first recharge claimed");
        GetFirstPayRewardResponse response = ReadClaim(claimId);
        AssertEqual(0, response.Code, "CN six-yuan recharge qualifies for the first recharge gift");
        AssertEqual(true, response.RewardList.Select(reward => reward.TemplateId).Order()
            .SequenceEqual([2_025_001, 2_994_001, 6_002_909, 6_210_106]), "CN first recharge grants both authored reward rows");
        AssertEqual(true, claimed.FirstRewardReceivedList.SequenceEqual([1, 2]), "claim push lists both reward rows");

        Player reloaded = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
        harness.Session.player = reloaded;
        NotifyPayInfo login = PayModule.BuildNotifyPayInfo(reloaded);
        AssertEqual(6f, login.TotalPayMoney, "CN login reports the paid amount in yuan");
        AssertEqual(true, login.FirstRewardReceivedList.SequenceEqual([1, 2]), "CN first recharge claims survive relogin");
        AssertEqual(40001013, Claim().Code, "CN first recharge gift cannot be granted twice");

        reloaded.ClientRegion = null;
        AssertEqual(600f, PayModule.BuildNotifyPayInfo(reloaded).TotalPayMoney, "shared client amount remains in hundredths");

        GetFirstPayRewardResponse Claim()
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(GetFirstPayRewardRequest), harness.Session, id, new GetFirstPayRewardRequest());
            return ReadClaim(id);
        }

        GetFirstPayRewardResponse ReadClaim(int id) =>
            ReadResponsePayload<GetFirstPayRewardResponse>(harness, id, nameof(GetFirstPayRewardResponse),
                "CN first recharge claim", maxPacketsToRead: 32);
    }
}
