using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidatePlayerPointPersistence()
    {
        const int shopPoint = 1015;
        const int socialPoint = 1021;
        int[] otherPoints = [2002, 2008];
        const int firstVisit = 1;
        const long playerId = 99_865;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(
            out var players, out _, out _, out _);
        BsonDocument oldSave = CreateDrawCompatibilityPlayer(playerId).ToBsonDocument();
        BsonDocument oldPlayerData = oldSave["player_data"].AsBsonDocument;
        AssertEqual(true, oldPlayerData.Contains(nameof(PlayerData.PointRecordInfo)), "current saves include the point records field");
        oldPlayerData.Remove(nameof(PlayerData.PointRecordInfo));
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId),
            BsonSerializer.Deserialize<Player>(oldSave), CreateDrawCompatibilityInventory(playerId, []), "player-point-test");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        int packetId = 86_500;

        AssertEqual(0, RecordPoint(shopPoint), "first shop visit acknowledgement");
        AssertEqual(1, players.ReplaceOneCalls, "first shop visit is saved before acknowledgement");
        AssertEqual(0, RecordPoint(shopPoint), "duplicate shop acknowledgement");
        AssertEqual(1, players.ReplaceOneCalls, "duplicate shop acknowledgement does not save");
        AssertEqual(1, RecordPoint(0), "invalid point id");
        AssertEqual(1, RecordPoint(shopPoint, 2), "a valid point rejects an unconfigured type");
        AssertEqual(1, RecordPoint(1018), "a display row that refers to another point cannot be acknowledged");
        AssertEqual(1, players.ReplaceOneCalls, "invalid point pairs do not save");

        players.ReplaceOneMatchedCount = 0;
        try
        {
            InvokePoint(socialPoint);
            throw new Exception("An unsuccessful point save must fail the request.");
        }
        catch (InvalidDataException exception) when (exception.InnerException is MongoException)
        {
        }
        players.ReplaceOneMatchedCount = 1;
        AssertEqual(0, RecordPoint(socialPoint), "different point can be recorded after a failed save");
        AssertEqual(3, players.ReplaceOneCalls, "retry persists the point again");

        harness.Session.player.ClientRegion = "cn";
        foreach (int pointId in otherPoints)
            AssertEqual(0, RecordPoint(pointId), $"CN can acknowledge point {pointId}");
        AssertEqual(5, players.ReplaceOneCalls, "both additional table-defined points are persisted");

        harness.Session.player = BsonSerializer.Deserialize<Player>(players.LastSuccessfulReplacementBson!);
        int[] expectedPoints = [shopPoint, socialPoint, .. otherPoints];
        foreach (string region in new[] { "en", "cn", "tw", "kr", "jp" })
        {
            harness.Session.player.ClientRegion = region;
            foreach (int pointId in expectedPoints)
                AssertEqual(0, RecordPoint(pointId), $"{region} can re-acknowledge point {pointId}");
        }
        AssertEqual(5, players.ReplaceOneCalls, "re-acknowledging valid points after relogin does not write");

        Type accountModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule");
        MethodInfo buildNotifyLogin = RequiredMethod(accountModule, "BuildNotifyLogin",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        NotifyLogin login = (NotifyLogin)buildNotifyLogin.Invoke(null, [harness.Session])!;
        JObject loginWire = JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(login)));
        JArray records = loginWire["PlayerData"]?["PointRecordInfo"] as JArray
            ?? throw new InvalidDataException("Login omitted PlayerData.PointRecordInfo.");
        AssertEqual(expectedPoints.Length, records.Count, "relogin includes each acknowledged point once");
        foreach (int pointId in expectedPoints)
        {
            JToken record = records.Single(entry => entry.Value<int>("Id") == pointId);
            AssertIntegerList([firstVisit], record["PointTypes"]!.Values<long>().ToArray(), $"point types for {pointId}");
        }

        void InvokePoint(int pointId, int pointType = firstVisit) =>
            InvokeRegisteredRequestHandler(nameof(RecordPlayerPointRequest), harness.Session, ++packetId,
                new RecordPlayerPointRequest { PointId = pointId, PointType = pointType });

        int RecordPoint(int pointId, int pointType = firstVisit)
        {
            InvokePoint(pointId, pointType);
            return ReadResponsePayload<RecordPlayerPointResponse>(harness, packetId, nameof(RecordPlayerPointResponse),
                $"acknowledgement for point {pointId}").Code;
        }
    }
}
