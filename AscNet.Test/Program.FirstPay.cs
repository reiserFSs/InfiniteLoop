using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using MessagePack;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateCnFirstPayCompatibility()
    {
        using MongoCollectionOverride storage = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players, out _, out _);
        using MongoCollectionOverride stageStorage = MongoCollectionOverride.InstallNoOpStageCollection();
        const long uid = 468703;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.Token = "cn-first-pay-compat-token";
        player.ClientRegion = "cn";
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), "cn-first-pay");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int packetId = 468730;

        Handshake(harness, "4.8.0", "4.8.12");
        player.TotalPayMoney = 9_999;
        AssertEqual(9_999f, NotifyFirstPay().TotalPayMoney, "CN 4.8 reports hundredths");
        AssertEqual(40001014, Claim().Code, "CN 4.8 requires 10000 hundredths");
        Handshake(harness, "", "");
        AssertEqual(9_999f, NotifyFirstPay().TotalPayMoney, "old saves without any version keep the legacy amount");

        Handshake(harness, "", "4.9.10");
        AssertEqual(true, PayModule.RememberCnFirstPayClientVersion(player, harness.Session.ClientApplicationVersion,
            harness.Session.ClientDocumentVersion), "CN login records a valid document version when the application version is absent");
        AssertEqual(false, PayModule.RememberCnFirstPayClientVersion(player, "", "4.9.10"), "same build does not require another save");
        player.TotalPayMoney = 0;
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

        Player reconnectPlayer = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
        using LoopbackSessionHarness reconnect = new(harness.Session.character, reconnectPlayer,
            harness.Session.inventory, "cn-first-pay-reconnect");
        reconnect.Session.stage = harness.Session.stage;
        int reconnectId = ++packetId;
        InvokeRegisteredRequestHandler(nameof(ReconnectRequest), reconnect.Session, reconnectId,
            new ReconnectRequest { PlayerId = (uint)uid, Token = reconnectPlayer.Token, LastMsgSeqNo = 0 });
        AssertEqual(0, ReadResponsePayload<ReconnectResponse>(reconnect, reconnectId, nameof(ReconnectResponse),
            "CN reconnect without handshake").Code, "reconnect accepts the existing session token");
        AssertEqual(6f, PayModule.BuildNotifyPayInfo(reconnectPlayer, reconnect.Session.ClientApplicationVersion,
            reconnect.Session.ClientDocumentVersion).TotalPayMoney, "CN reconnect keeps the last authenticated amount format");

        int claimId = ++packetId;
        InvokeRegisteredRequestHandler(nameof(GetFirstPayRewardRequest), reconnect.Session, claimId, new GetFirstPayRewardRequest());
        NotifyPayInfo claimed = ReadFirstPayPushAfterReconnect();
        GetFirstPayRewardResponse response = ReadClaim(claimId, reconnect);
        AssertEqual(0, response.Code, "CN six-yuan recharge still qualifies after reconnect");
        AssertEqual(true, response.RewardList.Select(reward => reward.TemplateId).Order()
            .SequenceEqual([2_025_001, 2_994_001, 6_002_909, 6_210_106]), "CN first recharge grants both authored reward rows");
        AssertEqual(true, claimed.FirstRewardReceivedList.SequenceEqual([1, 2]), "claim push lists both reward rows");

        Player reloaded = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
        harness.Session.player = reloaded;
        Handshake(harness, "4.9.0", "4.9.10");
        NotifyPayInfo login = NotifyFirstPay();
        AssertEqual(6f, login.TotalPayMoney, "CN login reports the paid amount in yuan");
        AssertEqual(true, login.FirstRewardReceivedList.SequenceEqual([1, 2]), "CN first recharge claims survive relogin");
        AssertEqual(40001013, Claim().Code, "CN first recharge gift cannot be granted twice");

        Handshake(harness, "4.8.0", "4.8.12");
        AssertEqual(600f, NotifyFirstPay().TotalPayMoney, "same saved amount after switching to CN 4.8");
        AssertEqual(40001013, Claim().Code, "switching versions does not grant the reward again");
        AssertEqual(true, PayModule.RememberCnFirstPayClientVersion(reloaded, harness.Session.ClientApplicationVersion,
            harness.Session.ClientDocumentVersion), "a new authenticated 4.8 login replaces the previously recorded 4.9 version");
        Handshake(harness, "", "");
        AssertEqual(600f, NotifyFirstPay().TotalPayMoney, "CN reconnect after a 4.8 login keeps the legacy amount");

        Handshake(harness, "4.9.1", "4.9.10");
        foreach (string? region in new[] { null, "en", "tw", "kr", "jp" })
        {
            reloaded.ClientRegion = region;
            AssertEqual(600f, NotifyFirstPay().TotalPayMoney, $"shared amount is unchanged for {region ?? "unresolved region"}");
        }

        const long legacyUid = uid + 1;
        Player legacyPlayer = CreateDrawCompatibilityPlayer(legacyUid);
        legacyPlayer.ClientRegion = "cn";
        legacyPlayer.TotalPayMoney = 10_000;
        using LoopbackSessionHarness legacy = new(CreateDrawCompatibilityCharacter(legacyUid), legacyPlayer,
            CreateDrawCompatibilityInventory(legacyUid, []), "cn-4.8-first-pay");
        legacy.Session.stage = CreateLoginAccountCompatibilityStage(legacyUid);
        Handshake(legacy, "4.8.0", "4.8.12");
        int legacyClaimId = ++packetId;
        InvokeRegisteredRequestHandler(nameof(GetFirstPayRewardRequest), legacy.Session, legacyClaimId, new GetFirstPayRewardRequest());
        NotifyPayInfo legacyClaimed = ReadPushPayload<NotifyPayInfo>(legacy, nameof(NotifyPayInfo), "CN 4.8 first recharge claimed");
        GetFirstPayRewardResponse legacyResponse = ReadResponsePayload<GetFirstPayRewardResponse>(legacy, legacyClaimId,
            nameof(GetFirstPayRewardResponse), "CN 4.8 first recharge claim", maxPacketsToRead: 32);
        AssertEqual(0, legacyResponse.Code, "CN 4.8 qualifies at 10000 hundredths");
        AssertEqual(10_000f, legacyClaimed.TotalPayMoney, "CN 4.8 claim notification uses hundredths");
        AssertEqual(true, legacyClaimed.FirstRewardReceivedList.SequenceEqual([1, 2]), "CN 4.8 keeps its authored claim IDs");

        void Handshake(LoopbackSessionHarness target, string applicationVersion, string documentVersion)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(HandshakeRequest), target.Session, id,
                new HandshakeRequest { ApplicationVersion = applicationVersion, DocumentVersion = documentVersion, Sha1 = "" });
            AssertEqual(0, ReadResponsePayload<HandshakeResponse>(target, id, nameof(HandshakeResponse), "client versions").Code,
                "handshake accepts the version metadata");
        }

        NotifyPayInfo NotifyFirstPay() => PayModule.BuildNotifyPayInfo(harness.Session.player,
            harness.Session.ClientApplicationVersion, harness.Session.ClientDocumentVersion);

        NotifyPayInfo ReadFirstPayPushAfterReconnect()
        {
            for (int index = 0; index < 32; index++)
            {
                var push = ReadStartupPush(reconnect, $"post-reconnect push {index}");
                if (push.Name == nameof(NotifyPayInfo))
                    return MessagePackSerializer.Deserialize<NotifyPayInfo>(push.Content);
            }
            throw new InvalidDataException("Reconnect claim did not send NotifyPayInfo.");
        }

        GetFirstPayRewardResponse Claim()
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(GetFirstPayRewardRequest), harness.Session, id, new GetFirstPayRewardRequest());
            return ReadClaim(id);
        }

        GetFirstPayRewardResponse ReadClaim(int id, LoopbackSessionHarness? target = null) =>
            ReadResponsePayload<GetFirstPayRewardResponse>(target ?? harness, id, nameof(GetFirstPayRewardResponse),
                "CN first recharge claim", maxPacketsToRead: 32);
    }
}
